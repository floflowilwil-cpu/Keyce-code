using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Extensions;

public sealed partial class ExtensionManager : IExtensionService
{
    private const string ManifestFileName = "extension.json";
    private const string StateFileName = "state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    // L'id sert de nom de dossier : on refuse "..", les slashs, etc.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*$")]
    private static partial Regex ValidIdRegex();

    private readonly string _root;
    private readonly Dictionary<string, ExtensionInfo> _extensions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public ExtensionManager(string extensionsRoot)
    {
        if (string.IsNullOrWhiteSpace(extensionsRoot))
            throw new ArgumentException(
                "Le dossier des extensions est obligatoire.", nameof(extensionsRoot));

        _root = extensionsRoot;
    }

    public event EventHandler<ExtensionChangedEventArgs>? ExtensionChanged;

    public IReadOnlyList<ExtensionInfo> Extensions
    {
        get
        {
            lock (_lock)
                return _extensions.Values.ToList();
        }
    }

    public async Task LoadInstalledAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        HashSet<string> activeIds = await ReadActiveIdsAsync(ct);
        var loaded = new List<ExtensionInfo>();

        foreach (string directory in Directory.GetDirectories(_root))
        {
            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                ExtensionManifest manifest = await ReadManifestAsync(manifestPath, ct);
                ExtensionState state = activeIds.Contains(manifest.Id)
                    ? ExtensionState.Active
                    : ExtensionState.Installed;

                loaded.Add(new ExtensionInfo(manifest, state, directory));
            }
            catch (AppException)
            {
                // Manifeste invalide : l'extension est ignorée.
            }
        }

        lock (_lock)
        {
            _extensions.Clear();
            foreach (ExtensionInfo info in loaded)
                _extensions[info.Manifest.Id] = info;
        }
    }

    public async Task<ExtensionInfo> InstallAsync(
        string sourceFolder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
            throw new AppException(
                ErrorCode.InvalidArgument, "Le dossier source est obligatoire.");

        string manifestPath = Path.Combine(sourceFolder, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new AppException(
                ErrorCode.NotFound,
                $"{ManifestFileName} est introuvable dans : {sourceFolder}");

        ExtensionManifest manifest = await ReadManifestAsync(manifestPath, ct);

        lock (_lock)
        {
            if (_extensions.ContainsKey(manifest.Id))
                throw new AppException(
                    ErrorCode.AlreadyExists,
                    $"L'extension '{manifest.Id}' est déjà installée.");
        }

        string target = Path.Combine(_root, manifest.Id);
        Directory.CreateDirectory(_root);
        CopyDirectory(sourceFolder, target);

        var info = new ExtensionInfo(manifest, ExtensionState.Installed, target);
        lock (_lock)
            _extensions[manifest.Id] = info;

        Raise(ExtensionChangeKind.Installed, info);
        return info;
    }

    public async Task UninstallAsync(string extensionId, CancellationToken ct = default)
    {
        ExtensionInfo info;
        lock (_lock)
        {
            if (!_extensions.TryGetValue(extensionId, out ExtensionInfo? current))
                throw new AppException(
                    ErrorCode.NotFound, $"Extension introuvable : {extensionId}");

            info = current;
            _extensions.Remove(extensionId);
        }

        if (Directory.Exists(info.InstallPath))
            Directory.Delete(info.InstallPath, recursive: true);

        await SaveStateAsync(ct);
        Raise(ExtensionChangeKind.Uninstalled, info with { State = ExtensionState.Installed });
    }

    public Task ActivateAsync(string extensionId, CancellationToken ct = default) =>
        SetStateAsync(extensionId, ExtensionState.Active, ExtensionChangeKind.Activated, ct);

    public Task DeactivateAsync(string extensionId, CancellationToken ct = default) =>
        SetStateAsync(extensionId, ExtensionState.Installed, ExtensionChangeKind.Deactivated, ct);

    private async Task SetStateAsync(
        string extensionId,
        ExtensionState newState,
        ExtensionChangeKind kind,
        CancellationToken ct)
    {
        ExtensionInfo updated;
        lock (_lock)
        {
            if (!_extensions.TryGetValue(extensionId, out ExtensionInfo? current))
                throw new AppException(
                    ErrorCode.NotFound, $"Extension introuvable : {extensionId}");

            if (current.State == newState)
                return;

            updated = current with { State = newState };
            _extensions[extensionId] = updated;
        }

        await SaveStateAsync(ct);
        Raise(kind, updated);
    }

    private static async Task<ExtensionManifest> ReadManifestAsync(
        string path, CancellationToken ct)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            ExtensionManifest? manifest =
                await JsonSerializer.DeserializeAsync<ExtensionManifest>(stream, JsonOptions, ct);

            if (manifest is null
                || string.IsNullOrWhiteSpace(manifest.Id)
                || string.IsNullOrWhiteSpace(manifest.Name)
                || string.IsNullOrWhiteSpace(manifest.Version))
            {
                throw new AppException(
                    ErrorCode.InvalidArgument,
                    "Manifeste invalide : id, name et version sont obligatoires.");
            }

            if (!ValidIdRegex().IsMatch(manifest.Id))
                throw new AppException(
                    ErrorCode.InvalidArgument,
                    "Identifiant invalide : lettres, chiffres, '.', '-' et '_' uniquement.");

            return manifest with { Permissions = manifest.Permissions ?? [] };
        }
        catch (JsonException ex)
        {
            throw new AppException(
                ErrorCode.InvalidArgument,
                $"Le fichier {ManifestFileName} est mal formé.", ex);
        }
    }

    private async Task<HashSet<string>> ReadActiveIdsAsync(CancellationToken ct)
    {
        string path = Path.Combine(_root, StateFileName);
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string json = await File.ReadAllTextAsync(path, ct);
            string[] ids = JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        string[] activeIds;
        lock (_lock)
        {
            activeIds = _extensions.Values
                .Where(e => e.State == ExtensionState.Active)
                .Select(e => e.Manifest.Id)
                .ToArray();
        }

        Directory.CreateDirectory(_root);

        await _saveGate.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(_root, StateFileName),
                JsonSerializer.Serialize(activeIds, JsonOptions),
                ct);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private void Raise(ExtensionChangeKind kind, ExtensionInfo info) =>
        ExtensionChanged?.Invoke(this, new ExtensionChangedEventArgs(kind, info));
}