using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>
/// API publique des extensions : chaque extension (.dll) contient une classe
/// publique avec un constructeur sans paramètre qui implémente cette interface.
/// </summary>
public interface IExtension
{
    /// <summary>Appelé quand l'extension est activée.</summary>
    Task ActivateAsync(IExtensionContext context, CancellationToken ct = default);

    /// <summary>Appelé quand l'extension est désactivée ou désinstallée.</summary>
    Task DeactivateAsync(CancellationToken ct = default);
}

/// <summary>
/// Ce que l'application met à disposition d'une extension active.
/// Chaque accès est contrôlé par les permissions du manifeste
/// (sinon <see cref="Errors.AppException"/> avec AccessDenied).
/// </summary>
public interface IExtensionContext
{
    ExtensionManifest Manifest { get; }

    /// <summary>Dossier où l'extension est installée.</summary>
    string InstallPath { get; }

    bool HasPermission(string permission);

    /// <summary>
    /// Ajoute une commande (permission "commands"). L'id doit commencer par
    /// "&lt;id de l'extension&gt;." et la commande est retirée à la désactivation.
    /// </summary>
    void RegisterCommand(string commandId, string title, Func<CancellationToken, Task> handler);

    /// <summary>Accès à Git (permission "git").</summary>
    IGitService Git { get; }

    /// <summary>Accès à l'espace de travail (permission "workspace").</summary>
    IWorkspaceService Workspace { get; }
}
