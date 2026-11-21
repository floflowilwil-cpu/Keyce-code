using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Extensions.Catalog;

/// <summary>
/// Catalogue d'extensions : recherche en ligne sur Open VSX (style VS Code)
/// avec téléchargement local, ou lecture d'un dossier catalogue local.
/// </summary>
public sealed partial class ExtensionCatalogService : IExtensionCatalogService
{
    private const string OpenVsxBaseUrl = "https://open-vsx.org/api";
    private const long MaxFullVsixBytes = 25_000_000;

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*$")]
    private static partial Regex ValidIdRegex();

    private readonly string _catalogRoot;
    private readonly IExtensionService _extensionService;
    private readonly HttpClient? _httpClient;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly ConcurrentDictionary<string, OnlinePackage> _onlineCache =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<ExtensionDownloadProgress>? DownloadProgressChanged;

    public ExtensionCatalogService(
        string catalogRoot,
        IExtensionService extensionService,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(catalogRoot))
            throw new ArgumentException(
                "Le dossier du catalogue est obligatoire.", nameof(catalogRoot));

        _catalogRoot = catalogRoot;
        _extensionService = extensionService
            ?? throw new ArgumentNullException(nameof(extensionService));
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<ExtensionCatalogEntry>> SearchAsync(
        string? query = null,
        CancellationToken ct = default)
    {
        if (_httpClient is not null)
        {
            IReadOnlyList<ExtensionCatalogEntry>? online = await TrySearchOnlineAsync(query, ct);
            if (online is not null && online.Count > 0)
                return online;
        }

        return await SearchLocalAsync(query, ct);
    }

    public async Task<IReadOnlyList<ExtensionUpdateInfo>> CheckUpdatesAsync(
        CancellationToken ct = default)
    {
        Dictionary<string, ExtensionCatalogEntry> byId = new(StringComparer.OrdinalIgnoreCase);

        foreach (OnlinePackage pkg in _onlineCache.Values)
            RegisterLatest(byId, new ExtensionCatalogEntry(pkg.Manifest, Path.Combine(_catalogRoot, pkg.Manifest.Id)));

        foreach (ExtensionCatalogEntry entry in await SearchLocalAsync(null, ct))
            RegisterLatest(byId, entry);

        var updates = new List<ExtensionUpdateInfo>();
        foreach (ExtensionInfo installed in _extensionService.Extensions)
        {
            if (!byId.TryGetValue(installed.Manifest.Id, out ExtensionCatalogEntry? available))
                continue;

            if (Version.TryParse(available.Manifest.Version, out Version? availVer)
                && Version.TryParse(installed.Manifest.Version, out Version? instVer)
                && availVer > instVer)
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
        ExtensionCatalogEntry entry = await ResolvePackageForInstallAsync(extensionId, ct);
        return await _extensionService.InstallAsync(entry.PackagePath, ct);
    }

    public async Task<ExtensionInfo> UpdateFromCatalogAsync(
        string extensionId,
        CancellationToken ct = default)
    {
        ExtensionCatalogEntry entry = await ResolvePackageForInstallAsync(extensionId, ct);
        return await _extensionService.UpdateAsync(entry.PackagePath, ct);
    }

    private async Task<IReadOnlyList<ExtensionCatalogEntry>?> TrySearchOnlineAsync(
        string? query,
        CancellationToken ct)
    {
        string q = query?.Trim() ?? string.Empty;
        string url = $"{OpenVsxBaseUrl}/-/search?query={Uri.EscapeDataString(q)}&size=20";

        try
        {
            using HttpResponseMessage response = await _httpClient!.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("extensions", out JsonElement extensions)
                || extensions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var results = new List<ExtensionCatalogEntry>();
            foreach (JsonElement item in extensions.EnumerateArray())
            {
                OnlinePackage? parsed = ParseOnlinePackage(item);
                if (parsed is null)
                    continue;

                _onlineCache[parsed.Manifest.Id] = parsed;
                results.Add(new ExtensionCatalogEntry(
                    parsed.Manifest,
                    Path.Combine(_catalogRoot, parsed.Manifest.Id)));
            }

            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<ExtensionCatalogEntry> ResolvePackageForInstallAsync(
        string extensionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new AppException(
                ErrorCode.InvalidArgument, "L'identifiant de l'extension est obligatoire.");

        await _installGate.WaitAsync(ct);
        try
        {
            if (_httpClient is not null)
            {
                OnlinePackage? pkg = await GetOrFetchOnlineMetadataAsync(extensionId, ct);
                if (pkg is not null)
                    return await DownloadPackageAsync(pkg, ct);
            }

            return await FindLocalEntryAsync(extensionId, ct);
        }
        finally
        {
            _installGate.Release();
        }
    }

    private async Task<OnlinePackage?> GetOrFetchOnlineMetadataAsync(
        string extensionId,
        CancellationToken ct)
    {
        if (_onlineCache.TryGetValue(extensionId, out OnlinePackage? cached))
            return cached;

        return await FetchOnlineDetailAsync(extensionId, ct);
    }

    private async Task<OnlinePackage?> FetchOnlineDetailAsync(
        string extensionId,
        CancellationToken ct)
    {
        int dot = extensionId.IndexOf('.');
        if (dot <= 0 || dot >= extensionId.Length - 1)
            return null;

        string ns = extensionId[..dot];
        string name = extensionId[(dot + 1)..];
        string url = $"{OpenVsxBaseUrl}/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";

        try
        {
            using HttpResponseMessage response = await _httpClient!.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            OnlinePackage? pkg = ParseOnlinePackage(doc.RootElement);
            if (pkg is not null)
                _onlineCache[pkg.Manifest.Id] = pkg;

            return pkg;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<ExtensionCatalogEntry> DownloadPackageAsync(
        OnlinePackage pkg,
        CancellationToken ct)
    {
        string packageDir = Path.Combine(_catalogRoot, pkg.Manifest.Id);
        string? tempVsixPath = null;

        try
        {
            ReportProgress(
                pkg.Manifest.Id,
                $"Connexion au Marketplace Open VSX pour {pkg.Manifest.Name}…",
                5);

            Directory.CreateDirectory(packageDir);

            string manifestJson = JsonSerializer.Serialize(
                new
                {
                    id = pkg.Manifest.Id,
                    name = pkg.Manifest.Name,
                    version = pkg.Manifest.Version,
                    description = pkg.Manifest.Description,
                    permissions = pkg.Manifest.Permissions
                },
                new JsonSerializerOptions { WriteIndented = true });

            await WriteTextAtomicAsync(
                Path.Combine(packageDir, ExtensionManager.ManifestFileName),
                manifestJson,
                ct);

            string infoJson = JsonSerializer.Serialize(
                new
                {
                    source = "open-vsx",
                    url = pkg.PageUrl,
                    downloadUrl = pkg.DownloadUrl,
                    downloadedAt = DateTimeOffset.UtcNow
                },
                new JsonSerializerOptions { WriteIndented = true });

            await WriteTextAtomicAsync(
                Path.Combine(packageDir, "marketplace-info.json"),
                infoJson,
                ct);

            string? vsixPath = null;
            if (!string.IsNullOrWhiteSpace(pkg.DownloadUrl))
            {
                string fileName = Path.GetFileName(new Uri(pkg.DownloadUrl).AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = $"{pkg.Manifest.Id}.vsix";

                vsixPath = Path.Combine(packageDir, fileName);
                if (!File.Exists(vsixPath) || new FileInfo(vsixPath).Length == 0)
                {
                    using HttpResponseMessage response = await _httpClient!.GetAsync(
                        pkg.DownloadUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct);

                    if (response.IsSuccessStatusCode)
                    {
                        long? contentLength = response.Content.Headers.ContentLength;
                        if (contentLength is not null && contentLength.Value > MaxFullVsixBytes)
                        {
                            ReportProgress(
                                pkg.Manifest.Id,
                                $"Archive VSIX ({FormatMb(contentLength.Value)} Mo) — téléchargement ciblé des fichiers…",
                                45);
                        }
                        else
                        {
                            tempVsixPath = Path.Combine(packageDir, $".tmp-{Guid.NewGuid():N}.vsix");
                            bool downloadedWithinLimit = await DownloadWithProgressAsync(
                                pkg.Manifest.Id,
                                response.Content,
                                contentLength,
                                tempVsixPath,
                                MaxFullVsixBytes,
                                ct);

                            if (downloadedWithinLimit)
                                File.Move(tempVsixPath, vsixPath, overwrite: true);
                        }
                    }
                }
            }

            if (vsixPath is not null && File.Exists(vsixPath) && new FileInfo(vsixPath).Length > 0)
            {
                ReportProgress(pkg.Manifest.Id, "Extraction du contenu VSIX…", 80);
                ExtractVsixContents(vsixPath, packageDir);
            }

            await EnsureExtractedTextFilesAsync(pkg, packageDir, ct);
            ReportProgress(pkg.Manifest.Id, $"Paquet '{pkg.Manifest.Name}' prêt.", 95);

            return new ExtensionCatalogEntry(pkg.Manifest, packageDir);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or UriFormatException
            or IOException
            or UnauthorizedAccessException)
        {
            throw new AppException(
                ErrorCode.Unavailable,
                $"Impossible de télécharger '{pkg.Manifest.Name}' depuis le Marketplace : {ex.Message}",
                ex);
        }
        finally
        {
            if (tempVsixPath is not null && File.Exists(tempVsixPath))
            {
                try { File.Delete(tempVsixPath); }
                catch (IOException) { }
            }
        }
    }

    private void ReportProgress(string extensionId, string statusText, double percentage) =>
        DownloadProgressChanged?.Invoke(
            this,
            new ExtensionDownloadProgress(extensionId, statusText, Math.Clamp(percentage, 0, 100)));

    private static string FormatMb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    private static async Task WriteTextAtomicAsync(
        string targetPath,
        string content,
        CancellationToken ct)
    {
        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (IOException) { }
            }
        }
    }

    private async Task<bool> DownloadWithProgressAsync(
        string extensionId,
        HttpContent content,
        long? expectedBytes,
        string tempPath,
        long maxBytes,
        CancellationToken ct)
    {
        await using Stream networkStream = await content.ReadAsStreamAsync(ct);
        await using (FileStream fileStream = File.Create(tempPath))
        {
            byte[] buffer = new byte[32 * 1024];
            long total = 0;
            long lastReportTick = 0;
            int read;

            while ((read = await networkStream.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    return false;

                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);

                long now = Environment.TickCount64;
                if (now - lastReportTick >= 80)
                {
                    lastReportTick = now;
                    if (expectedBytes is > 0)
                    {
                        double ratio = (double)total / expectedBytes.Value;
                        int pct = (int)Math.Round(ratio * 100);
                        ReportProgress(
                            extensionId,
                            $"Téléchargement VSIX : {FormatMb(total)} / {FormatMb(expectedBytes.Value)} Mo ({pct} %)…",
                            10 + ratio * 65);
                    }
                    else
                    {
                        ReportProgress(
                            extensionId,
                            $"Téléchargement VSIX : {FormatMb(total)} Mo reçus…",
                            40);
                    }
                }
            }
        }

        return true;
    }

    private static void ExtractVsixContents(string vsixPath, string packageDir)
    {
        string fullPackageDir = Path.GetFullPath(packageDir);
        string rootPrefix = fullPackageDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int extraAssetCount = 0;

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(vsixPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.Length > 2_000_000)
                    continue;

                string normalized = entry.FullName.Replace('\\', '/');
                string? relativeTarget = MapVsixEntryToRelativePath(normalized, ref extraAssetCount);
                if (relativeTarget is null)
                    continue;

                string targetPath = Path.GetFullPath(Path.Combine(fullPackageDir, relativeTarget));
                if (!targetPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                using Stream src = entry.Open();
                using FileStream dst = File.Create(targetPath);
                src.CopyTo(dst);
            }
        }
        catch (InvalidDataException)
        {
            // Archive VSIX corrompue ou non ZIP : les fichiers metadata directs prennent le relais.
        }
        catch (IOException)
        {
            // Ignoré : les fichiers metadata directs complètent si besoin.
        }
    }

    private static string? MapVsixEntryToRelativePath(string normalizedPath, ref int extraAssetCount)
    {
        if (normalizedPath.Equals("extension/package.json", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            return "package.json";
        }

        if (normalizedPath.Equals("extension/package.nls.json", StringComparison.OrdinalIgnoreCase))
            return "package.nls.json";

        if (normalizedPath.Equals("extension.vsixmanifest", StringComparison.OrdinalIgnoreCase))
            return "extension.vsixmanifest";

        if (normalizedPath.Equals("extension/README.md", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Equals("extension/readme.md", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Equals("README.md", StringComparison.OrdinalIgnoreCase))
        {
            return "README.md";
        }

        if (normalizedPath.Equals("extension/CHANGELOG.md", StringComparison.OrdinalIgnoreCase))
            return "CHANGELOG.md";

        if (normalizedPath.StartsWith("extension/LICENSE", StringComparison.OrdinalIgnoreCase))
            return "LICENSE.txt";

        if (extraAssetCount < 30 && normalizedPath.StartsWith("extension/", StringComparison.OrdinalIgnoreCase))
        {
            string subPath = normalizedPath["extension/".Length..];
            if (subPath.StartsWith("themes/", StringComparison.OrdinalIgnoreCase)
                || subPath.StartsWith("snippets/", StringComparison.OrdinalIgnoreCase)
                || subPath.StartsWith("syntaxes/", StringComparison.OrdinalIgnoreCase))
            {
                extraAssetCount++;
                return subPath.Replace('/', Path.DirectorySeparatorChar);
            }
        }

        return null;
    }

    private async Task EnsureExtractedTextFilesAsync(
        OnlinePackage pkg,
        string packageDir,
        CancellationToken ct)
    {
        string pkgJsonPath = Path.Combine(packageDir, "package.json");
        string readmePath = Path.Combine(packageDir, "README.md");

        OnlinePackage detailed = pkg;
        if ((!File.Exists(pkgJsonPath) && string.IsNullOrWhiteSpace(detailed.ManifestUrl))
            || (!File.Exists(readmePath) && string.IsNullOrWhiteSpace(detailed.ReadmeUrl)))
        {
            OnlinePackage? fetched = await FetchOnlineDetailAsync(pkg.Manifest.Id, ct);
            if (fetched is not null)
                detailed = fetched;
        }

        if (!File.Exists(pkgJsonPath) && !string.IsNullOrWhiteSpace(detailed.ManifestUrl))
        {
            ReportProgress(pkg.Manifest.Id, "Téléchargement de package.json…", 88);
            await TryDownloadSmallTextFileAsync(detailed.ManifestUrl, pkgJsonPath, ct);
        }

        if (!File.Exists(readmePath) && !string.IsNullOrWhiteSpace(detailed.ReadmeUrl))
        {
            ReportProgress(pkg.Manifest.Id, "Téléchargement du README.md…", 92);
            await TryDownloadSmallTextFileAsync(detailed.ReadmeUrl, readmePath, ct);
        }

        if (!File.Exists(readmePath))
        {
            string fallbackReadme =
                $"# {pkg.Manifest.Name} (`{pkg.Manifest.Id}` v{pkg.Manifest.Version})\n\n"
                + $"{pkg.Manifest.Description}\n\n"
                + (string.IsNullOrWhiteSpace(pkg.PageUrl) ? string.Empty : $"Marketplace Open VSX : {pkg.PageUrl}\n");

            await WriteTextAtomicAsync(readmePath, fallbackReadme, ct);
        }
    }

    private async Task TryDownloadSmallTextFileAsync(string url, string targetPath, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient!.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return;

            string text = await response.Content.ReadAsStringAsync(ct);
            await WriteTextAtomicAsync(targetPath, text, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
        }
    }

    private async Task<IReadOnlyList<ExtensionCatalogEntry>> SearchLocalAsync(
        string? query,
        CancellationToken ct)
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

    private async Task<ExtensionCatalogEntry> FindLocalEntryAsync(
        string extensionId,
        CancellationToken ct)
    {
        IReadOnlyList<ExtensionCatalogEntry> entries = await SearchLocalAsync(null, ct);
        ExtensionCatalogEntry? entry = entries
            .Where(e => string.Equals(
                e.Manifest.Id, extensionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => Version.Parse(e.Manifest.Version))
            .FirstOrDefault();

        return entry ?? throw new AppException(
            ErrorCode.NotFound,
            $"Extension '{extensionId}' introuvable dans le catalogue.");
    }

    private static OnlinePackage? ParseOnlinePackage(JsonElement item)
    {
        string? ns = item.TryGetProperty("namespace", out JsonElement nsProp)
            ? nsProp.GetString()
            : null;
        string? name = item.TryGetProperty("name", out JsonElement nameProp)
            ? nameProp.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(name))
            return null;

        string id = $"{ns}.{name}";
        if (!ValidIdRegex().IsMatch(id))
            return null;

        string displayName = item.TryGetProperty("displayName", out JsonElement dispProp)
            && !string.IsNullOrWhiteSpace(dispProp.GetString())
                ? dispProp.GetString()!
                : name;

        string rawVersion = item.TryGetProperty("version", out JsonElement verProp)
            ? verProp.GetString() ?? "1.0.0"
            : "1.0.0";
        string version = NormalizeVersion(rawVersion);

        string description = item.TryGetProperty("description", out JsonElement descProp)
            ? descProp.GetString() ?? string.Empty
            : string.Empty;

        long downloads = item.TryGetProperty("downloadCount", out JsonElement dlCountProp)
            && dlCountProp.TryGetInt64(out long count)
                ? count
                : 0;

        string suffix = downloads > 0
            ? $" ({ns} • {downloads.ToString("N0", CultureInfo.InvariantCulture)} téléch.)"
            : $" ({ns})";

        string fullDescription = string.IsNullOrWhiteSpace(description)
            ? $"Extension publiée par {ns}{suffix}"
            : $"{description}{suffix}";

        string? downloadUrl = null;
        string? manifestUrl = null;
        string? readmeUrl = null;
        if (item.TryGetProperty("files", out JsonElement files)
            && files.ValueKind == JsonValueKind.Object)
        {
            downloadUrl = TryGetStringProperty(files, "download");
            manifestUrl = TryGetStringProperty(files, "manifest");
            readmeUrl = TryGetStringProperty(files, "readme");
        }

        string? pageUrl = TryGetStringProperty(item, "url");

        var manifest = new ExtensionManifest(
            id,
            displayName,
            version,
            fullDescription,
            [ExtensionPermissions.Commands]);

        return new OnlinePackage(manifest, downloadUrl, pageUrl, manifestUrl, readmeUrl);
    }

    private static string? TryGetStringProperty(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out JsonElement prop)
        && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string NormalizeVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return "1.0.0";

        string core = rawVersion.Trim().Split(['-', '+'], 2)[0];
        if (Version.TryParse(core, out _))
            return core;

        string[] segments = core.Split('.');
        if (segments.Length >= 2
            && int.TryParse(segments[0], out int major) && major >= 0
            && int.TryParse(segments[1], out int minor) && minor >= 0)
        {
            int build = segments.Length >= 3 && int.TryParse(segments[2], out int b) && b >= 0
                ? b
                : 0;
            return $"{major}.{minor}.{build}";
        }

        return "1.0.0";
    }

    private static void RegisterLatest(
        Dictionary<string, ExtensionCatalogEntry> byId,
        ExtensionCatalogEntry entry)
    {
        if (!byId.TryGetValue(entry.Manifest.Id, out ExtensionCatalogEntry? existing)
            || Version.Parse(entry.Manifest.Version) > Version.Parse(existing.Manifest.Version))
        {
            byId[entry.Manifest.Id] = entry;
        }
    }

    private static bool MatchesQuery(ExtensionManifest manifest, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return manifest.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || manifest.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || manifest.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record OnlinePackage(
        ExtensionManifest Manifest,
        string? DownloadUrl,
        string? PageUrl,
        string? ManifestUrl = null,
        string? ReadmeUrl = null);
}
