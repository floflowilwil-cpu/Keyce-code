using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Application.Workspace;

/// <summary>Gestion du dossier de travail actuellement ouvert.</summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private readonly object _sync = new();
    private WorkspaceInfo? _current;

    public WorkspaceInfo? Current
    {
        get
        {
            lock (_sync)
                return _current;
        }
    }

    public Task OpenWorkspaceAsync(string rootPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new AppException(ErrorCode.InvalidArgument, "Le chemin du dossier de travail est vide.");

        string fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath))
            throw new AppException(ErrorCode.NotFound, $"Dossier de travail introuvable : {fullPath}");

        WorkspaceInfo next = new(
            Path.TrimEndingDirectorySeparator(fullPath),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)) is { Length: > 0 } name
                ? name
                : fullPath);

        WorkspaceInfo? previous;
        lock (_sync)
        {
            previous = _current;
            if (previous?.RootPath.Equals(next.RootPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == true)
                return Task.CompletedTask;

            _current = next;
        }

        WorkspaceChanged(this, new WorkspaceChangedEventArgs(previous, next));
        return Task.CompletedTask;
    }

    public Task CloseWorkspaceAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        WorkspaceInfo? previous;
        lock (_sync)
        {
            previous = _current;
            _current = null;
        }

        if (previous is not null)
            WorkspaceChanged(this, new WorkspaceChangedEventArgs(previous, null));

        return Task.CompletedTask;
    }

    public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged = delegate { };
}
