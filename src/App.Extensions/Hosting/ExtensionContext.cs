using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Extensions.Hosting;

/// <summary>
/// Contexte donné à une extension active. Vérifie ses permissions
/// et retire tout ce qu'elle a enregistré quand elle s'arrête.
/// </summary>
internal sealed class ExtensionContext(
    ExtensionInfo info,
    ICommandService commands,
    IGitService? git,
    IWorkspaceService? workspace)
    : IExtensionContext, IDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private readonly object _lock = new();
    private bool _disposed;

    public ExtensionManifest Manifest { get; } = info.Manifest;

    public string InstallPath { get; } = info.InstallPath;

    public IGitService Git
    {
        get
        {
            Demand(ExtensionPermissions.Git);
            return git ?? throw Unavailable("Git");
        }
    }

    public IWorkspaceService Workspace
    {
        get
        {
            Demand(ExtensionPermissions.Workspace);
            return workspace ?? throw Unavailable("l'espace de travail");
        }
    }

    public bool HasPermission(string permission) =>
        Manifest.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public void RegisterCommand(
        string commandId, string title, Func<CancellationToken, Task> handler)
    {
        Demand(ExtensionPermissions.Commands);

        // Une extension ne peut créer que ses propres commandes : "monid.xxx".
        string prefix = Manifest.Id + ".";
        if (string.IsNullOrWhiteSpace(commandId)
            || !commandId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || commandId.Length == prefix.Length)
        {
            throw new AppException(
                ErrorCode.AccessDenied,
                $"L'extension '{Manifest.Id}' doit nommer ses commandes \"{prefix}...\" (reçu : '{commandId}').");
        }

        lock (_lock)
        {
            ThrowIfDisposed();
            _registrations.Add(commands.Register(commandId, title, handler, Manifest.Id));
        }
    }

    public void Dispose()
    {
        List<IDisposable> registrations;
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            registrations = [.. _registrations];
            _registrations.Clear();
        }

        foreach (IDisposable registration in registrations)
            registration.Dispose();
    }

    private void Demand(string permission)
    {
        lock (_lock)
            ThrowIfDisposed();

        if (!HasPermission(permission))
            throw new AppException(
                ErrorCode.AccessDenied,
                $"L'extension '{Manifest.Id}' n'a pas la permission \"{permission}\".");
    }

    private AppException Unavailable(string what) =>
        new(ErrorCode.Unavailable, $"Le service {what} n'est pas disponible pour les extensions.");

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new AppException(
                ErrorCode.Unavailable, $"L'extension '{Manifest.Id}' n'est plus active.");
    }
}
