using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Catalogue d'extensions : recherche, détection et installation des mises à jour.</summary>
public interface IExtensionCatalogService
{
    event EventHandler<ExtensionDownloadProgress>? DownloadProgressChanged;

    Task<IReadOnlyList<ExtensionCatalogEntry>> SearchAsync(
        string? query = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExtensionUpdateInfo>> CheckUpdatesAsync(
        CancellationToken ct = default);

    Task<ExtensionInfo> InstallFromCatalogAsync(
        string extensionId,
        CancellationToken ct = default);

    Task<ExtensionInfo> UpdateFromCatalogAsync(
        string extensionId,
        CancellationToken ct = default);
}
