using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Extensions.Catalog;

/// <summary>Catalogue local d'extensions (recherche, détection de mises à jour, installation).</summary>
public sealed class ExtensionCatalogService : IExtensionCatalogService
{
    private readonly string _catalogRoot;
    private readonly IExtensionService _extensionService;

    public ExtensionCatalogService(string catalogRoot, IExtensionService extensionService)
    {
        if (string.IsNullOrWhiteSpace(catalogRoot))
            throw new ArgumentException(
                "Le dossier du catalogue est obligatoire.", nameof(catalogRoot));

        _catalogRoot = catalogRoot;
        _extensionService = extensionService
            ?? throw new ArgumentNullException(nameof(extensionService));
    }

    public async Task<IReadOnlyList<ExtensionCatalogEntry>> SearchAsync(
        string? query = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_catalogRoot))
            return [];

        var entries = new List<ExtensionCatalogEntry>();

        foreach (string directory in Directory.GetDirectories(_catalogRoot))
        {
            ct.ThrowIfCancellationRequested();

            string manifestPath = Path.Combine(directory, ExtensionManager.ManifestFileName);
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                ExtensionManifest manifest =
                    await ExtensionManager.ReadManifestAsync(manifestPath, ct);

                if (MatchesQuery(manifest, query))
                    entries.Add(new ExtensionCatalogEntry(manifest, directory));
            }
            catch (AppException)
            {
                // Manifeste invalide : l'entrée du catalogue est ignorée.
            }
        }

        return entries
            .OrderBy(e => e.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<ExtensionUpdateInfo>> CheckUpdatesAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<ExtensionCatalogEntry> catalog = await SearchAsync(null, ct);
        Dictionary<string, ExtensionCatalogEntry> byId = new(StringComparer.OrdinalIgnoreCase);

        foreach (ExtensionCatalogEntry entry in catalog)
        {
            if (!byId.TryGetValue(entry.Manifest.Id, out ExtensionCatalogEntry? existing)
                || Version.Parse(entry.Manifest.Version) > Version.Parse(existing.Manifest.Version))
            {
                byId[entry.Manifest.Id] = entry;
            }
        }

        var updates = new List<ExtensionUpdateInfo>();
        foreach (ExtensionInfo installed in _extensionService.Extensions)
        {
            if (!byId.TryGetValue(installed.Manifest.Id, out ExtensionCatalogEntry? available))
                continue;

            if (Version.Parse(available.Manifest.Version) > Version.Parse(installed.Manifest.Version))
            {
                updates.Add(new ExtensionUpdateInfo(
                    installed.Manifest.Id,
                    installed.Manifest.Version,
                    available.Manifest.Version,
                    available.PackagePath));
            }
        }

        return updates;
    }

    public async Task<ExtensionInfo> InstallFromCatalogAsync(
        string extensionId,
        CancellationToken ct = default)
    {
        ExtensionCatalogEntry entry = await FindEntryAsync(extensionId, ct);
        return await _extensionService.InstallAsync(entry.PackagePath, ct);
    }

    public async Task<ExtensionInfo> UpdateFromCatalogAsync(
        string extensionId,
        CancellationToken ct = default)
    {
        ExtensionCatalogEntry entry = await FindEntryAsync(extensionId, ct);
        return await _extensionService.UpdateAsync(entry.PackagePath, ct);
    }

    private async Task<ExtensionCatalogEntry> FindEntryAsync(
        string extensionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new AppException(
                ErrorCode.InvalidArgument, "L'identifiant de l'extension est obligatoire.");

        IReadOnlyList<ExtensionCatalogEntry> entries = await SearchAsync(null, ct);
        ExtensionCatalogEntry? entry = entries
            .Where(e => string.Equals(
                e.Manifest.Id, extensionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => Version.Parse(e.Manifest.Version))
            .FirstOrDefault();

        return entry ?? throw new AppException(
            ErrorCode.NotFound,
            $"Extension '{extensionId}' introuvable dans le catalogue.");
    }

    private static bool MatchesQuery(ExtensionManifest manifest, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return manifest.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || manifest.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || manifest.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
