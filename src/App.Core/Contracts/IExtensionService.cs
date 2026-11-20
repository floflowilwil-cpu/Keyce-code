using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Gestion des extensions. Utilisé par UI et extensions.</summary>
public interface IExtensionService
{
    IReadOnlyList<ExtensionInfo> Extensions { get; }

    Task LoadInstalledAsync(CancellationToken ct = default);
    Task<ExtensionInfo> InstallAsync(string sourceFolder, CancellationToken ct = default);
    Task UninstallAsync(string extensionId, CancellationToken ct = default);
    Task ActivateAsync(string extensionId, CancellationToken ct = default);
    Task DeactivateAsync(string extensionId, CancellationToken ct = default);

    event EventHandler<ExtensionChangedEventArgs>? ExtensionChanged;
}