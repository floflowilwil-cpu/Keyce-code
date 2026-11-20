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

/// <summary>Ce que l'application met à disposition d'une extension active.</summary>
public interface IExtensionContext
{
    ExtensionManifest Manifest { get; }

    /// <summary>Dossier où l'extension est installée.</summary>
    string InstallPath { get; }

    /// <summary>
    /// Ajoute une commande. Elle est retirée automatiquement
    /// quand l'extension est désactivée.
    /// </summary>
    void RegisterCommand(string commandId, string title, Func<CancellationToken, Task> handler);
}
