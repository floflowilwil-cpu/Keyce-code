using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Extensions.Hosting;

/// <summary>Contexte donné à une extension active. Retire tout ce qu'elle a enregistré à la fin.</summary>
internal sealed class ExtensionContext(ExtensionInfo info, ICommandService commands)
    : IExtensionContext, IDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private readonly object _lock = new();
    private bool _disposed;

    public ExtensionManifest Manifest { get; } = info.Manifest;

    public string InstallPath { get; } = info.InstallPath;

    public void RegisterCommand(
        string commandId, string title, Func<CancellationToken, Task> handler)
    {
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new AppException(
                ErrorCode.Unavailable, $"L'extension '{Manifest.Id}' n'est plus active.");
    }
}
