using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Espace de travail (dossier ouvert). Utilisé par UI, fichiers, Git, langages.</summary>
public interface IWorkspaceService
{
    WorkspaceInfo? Current { get; }

    Task OpenWorkspaceAsync(string rootPath, CancellationToken ct = default);
    Task CloseWorkspaceAsync(CancellationToken ct = default);

    event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged;
}
