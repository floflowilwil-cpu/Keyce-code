using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Application.Workspaces;

/// <summary>
/// Espace de travail = le dossier ouvert. C'est la source de vérité unique : l'explorateur,
/// Git, le terminal et les langages lisent le dossier courant ici (jamais depuis l'interface).
/// </summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private readonly object _gate = new();

    public WorkspaceInfo? Current { get; private set; }

    public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged = delegate { };

    public Task OpenWorkspaceAsync(string rootPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new AppException(ErrorCode.InvalidArgument, "Le chemin du dossier est vide.");

        string full = Path.GetFullPath(rootPath);
        if (!Directory.Exists(full))
            throw new AppException(ErrorCode.NotFound, $"Dossier introuvable : {full}");

        string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var info = new WorkspaceInfo(full, string.IsNullOrEmpty(name) ? full : name);

        WorkspaceInfo? previous;
        lock (_gate)
        {
            previous = Current;
            Current = info;
        }

        WorkspaceChanged(this, new WorkspaceChangedEventArgs(previous, info));
        return Task.CompletedTask;
    }

    public Task CloseWorkspaceAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        WorkspaceInfo? previous;
        lock (_gate)
        {
            previous = Current;
            Current = null;
        }

        if (previous is not null)
            WorkspaceChanged(this, new WorkspaceChangedEventArgs(previous, null));
        return Task.CompletedTask;
    }
}
