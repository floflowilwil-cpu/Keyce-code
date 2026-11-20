using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;
using App.Extensions.Commands;
using App.Extensions.Hosting;

namespace App.Extensions;

public sealed partial class ExtensionManager : IExtensionService
{
    internal const string ManifestFileName = "extension.json";
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
    private readonly IGitService? _git;
    private readonly IWorkspaceService? _workspace;
    private readonly Version _appVersion;
    private readonly Dictionary<string, ExtensionInfo> _extensions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    // Une seule opération (installation, activation...) à la fois.
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    // Extensions dont le code est chargé et activé (protégé par _operationGate).
    private readonly Dictionary<string, RunningExtension> _running =
        new(StringComparer.OrdinalIgnoreCase);

    public ExtensionManager(
        string extensionsRoot,
        ICommandService? commands = null,
        IGitService? git = null,
        IWorkspaceService? workspace = null,
        Version? appVersion = null)
    {
        if (string.IsNullOrWhiteSpace(extensionsRoot))
            throw new ArgumentException(
                "Le dossier des extensions est obligatoire.", nameof(extensionsRoot));

        _root = extensionsRoot;
        _git = git;
        _workspace = workspace;
        _appVersion = appVersion ?? new Version(1, 0, 0);
        Commands = commands ?? new CommandService();
    }

    public event EventHandler<ExtensionChangedEventArgs>? ExtensionChanged;

    /// <summary>Registre où les extensions ajoutent leurs commandes.</summary>
    public ICommandService Commands { get; }

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
        await _operationGate.WaitAsync(ct);
        try
        {
            foreach (string id in _running.Keys.ToList())
                await StopAsync(id, ct);

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

            // On relance le code des extensions qui étaient actives.
            bool stateChanged = false;
            foreach (ExtensionInfo info in loaded.Where(e => e.State == ExtensionState.Active))
            {
                try
                {
                    await StartAsync(info, ct);
                }
                catch (AppException)
                {
                    // Une extension qui plante au démarrage ne bloque pas les autres.
                    lock (_lock)
                        _extensions[info.Manifest.Id] = info with { State = ExtensionState.Installed };
                    stateChanged = true;
                }
            }

            if (stateChanged)
                await SaveStateAsync(ct);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ExtensionInfo> InstallAsync(
        string sourceFolder, CancellationToken ct = default)
    {
        ExtensionManifest manifest = await ReadSourceManifestAsync(sourceFolder, ct);
        EnsureCompatibleWithApp(manifest);

        ExtensionInfo info;
        await _operationGate.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                if (_extensions.ContainsKey(manifest.Id))
                    throw new AppException(
                        ErrorCode.AlreadyExists,
                        $"L'extension '{manifest.Id}' est déjà installée.");

                EnsureDependencies_NoLock(manifest, requireActive: false);
            }

            string target = Path.Combine(_root, manifest.Id);
            Directory.CreateDirectory(_root);
            CopyDirectory(sourceFolder, target);

            info = new ExtensionInfo(manifest, ExtensionState.Installed, target);
            lock (_lock)
                _extensions[manifest.Id] = info;
        }
        finally
        {
            _operationGate.Release();
        }

        Raise(ExtensionChangeKind.Installed, info);
        return info;
    }

    public async Task<ExtensionInfo> UpdateAsync(
        string sourceFolder, CancellationToken ct = default)
    {
        ExtensionManifest manifest = await ReadSourceManifestAsync(sourceFolder, ct);
        EnsureCompatibleWithApp(manifest);

        ExtensionInfo updated;
        await _operationGate.WaitAsync(ct);
        try
        {
            ExtensionInfo current = GetExtension(manifest.Id);

            if (Version.Parse(manifest.Version) <= Version.Parse(current.Manifest.Version))
            {
                throw new AppException(
                    ErrorCode.Conflict,
                    $"La version {manifest.Version} n'est pas plus récente que la version installée ({current.Manifest.Version}).");
            }

            lock (_lock)
            {
                EnsureDependencies_NoLock(
                    manifest,
                    requireActive: current.State == ExtensionState.Active);
            }

            if (current.State == ExtensionState.Active)
                await StopAsync(manifest.Id, ct);

            if (Directory.Exists(current.InstallPath))
                Directory.Delete(current.InstallPath, recursive: true);

            CopyDirectory(sourceFolder, current.InstallPath);

            updated = new ExtensionInfo(manifest, current.State, current.InstallPath);

            if (current.State == ExtensionState.Active)
            {
                try
                {
                    await StartAsync(updated, ct);
                }
                catch
                {
                    lock (_lock)
                        _extensions[manifest.Id] = updated with { State = ExtensionState.Installed };
                    await SaveStateAsync(CancellationToken.None);
                    throw;
                }
            }

            lock (_lock)
                _extensions[manifest.Id] = updated;
        }
        finally
        {
            _operationGate.Release();
        }

        Raise(ExtensionChangeKind.Updated, updated);
        return updated;
    }

    public async Task UninstallAsync(string extensionId, CancellationToken ct = default)
    {
        ExtensionInfo info;
        await _operationGate.WaitAsync(ct);
        try
        {
            info = GetExtension(extensionId);
            lock (_lock)
                EnsureNoDependents_NoLock(info.Manifest.Id, activeOnly: false, "désinstaller");

            await StopAsync(info.Manifest.Id, ct);

            lock (_lock)
                _extensions.Remove(info.Manifest.Id);

            if (Directory.Exists(info.InstallPath))
                Directory.Delete(info.InstallPath, recursive: true);

            await SaveStateAsync(ct);
        }
        finally
        {
            _operationGate.Release();
        }

        Raise(ExtensionChangeKind.Uninstalled, info with { State = ExtensionState.Installed });
    }

    public async Task ActivateAsync(string extensionId, CancellationToken ct = default)
    {
        ExtensionInfo updated;
        await _operationGate.WaitAsync(ct);
        try
        {
            ExtensionInfo current = GetExtension(extensionId);
            if (current.State == ExtensionState.Active)
                return;

            EnsureCompatibleWithApp(current.Manifest);
            lock (_lock)
                EnsureDependencies_NoLock(current.Manifest, requireActive: true);

            // Si le code de l'extension échoue, elle reste "Installed".
            await StartAsync(current, ct);

            updated = current with { State = ExtensionState.Active };
            lock (_lock)
                _extensions[current.Manifest.Id] = updated;

            await SaveStateAsync(ct);
        }
        finally
        {
            _operationGate.Release();
        }

        Raise(ExtensionChangeKind.Activated, updated);
    }

    public async Task DeactivateAsync(string extensionId, CancellationToken ct = default)
    {
        ExtensionInfo updated;
        await _operationGate.WaitAsync(ct);
        try
        {
            ExtensionInfo current = GetExtension(extensionId);
            if (current.State == ExtensionState.Installed)
                return;

            lock (_lock)
                EnsureNoDependents_NoLock(current.Manifest.Id, activeOnly: true, "désactiver");

            await StopAsync(current.Manifest.Id, ct);

            updated = current with { State = ExtensionState.Installed };
            lock (_lock)
                _extensions[current.Manifest.Id] = updated;

            await SaveStateAsync(ct);
        }
        finally
        {
            _operationGate.Release();
        }

        Raise(ExtensionChangeKind.Deactivated, updated);
    }

    private ExtensionInfo GetExtension(string extensionId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(extensionId)
                || !_extensions.TryGetValue(extensionId, out ExtensionInfo? info))
            {
                throw new AppException(
                    ErrorCode.NotFound, $"Extension introuvable : {extensionId}");
            }

            return info;
        }
    }

    // Charge la .dll de l'extension (si elle en a une) et appelle ActivateAsync.
    private async Task StartAsync(ExtensionInfo info, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(info.Manifest.Main))
            return; // Extension sans code (thème, extraits...) : rien à exécuter.

        string mainPath = ResolveMainPath(info);
        var loadContext = new ExtensionLoadContext(info.InstallPath, info.Manifest.Id);
        var context = new ExtensionContext(info, Commands, _git, _workspace);

        void Cleanup()
        {
            context.Dispose();
            loadContext.Unload();
        }

        try
        {
            Assembly assembly = loadContext.LoadMain(mainPath);
            IExtension extension = CreateExtension(assembly, info.Manifest);

            await extension.ActivateAsync(context, ct);

            _running[info.Manifest.Id] = new RunningExtension(extension, context, loadContext);
        }
        catch (AppException)
        {
            Cleanup();
            throw;
        }
        catch (OperationCanceledException)
        {
            Cleanup();
            throw;
        }
        catch (Exception ex)
        {
            Cleanup();
            throw new AppException(
                ErrorCode.Unknown,
                $"L'extension '{info.Manifest.Id}' n'a pas pu être activée : {ex.Message}",
                ex);
        }
    }

    private async Task StopAsync(string extensionId, CancellationToken ct)
    {
        if (!_running.Remove(extensionId, out RunningExtension? running))
            return;

        try
        {
            await running.Extension.DeactivateAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Une extension qui plante à l'arrêt ne doit pas bloquer l'application.
        }
        finally
        {
            running.Context.Dispose();
            running.LoadContext.Unload();
        }
    }

    private static string ResolveMainPath(ExtensionInfo info)
    {
        string folder = Path.GetFullPath(info.InstallPath);
        string mainPath = Path.GetFullPath(Path.Combine(folder, info.Manifest.Main!));

        // La .dll doit rester dans le dossier de l'extension.
        if (!mainPath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                ErrorCode.InvalidArgument,
                $"Le fichier principal de '{info.Manifest.Id}' doit être dans son dossier.");

        if (!File.Exists(mainPath))
            throw new AppException(
                ErrorCode.NotFound,
                $"Fichier principal introuvable pour '{info.Manifest.Id}' : {info.Manifest.Main}");

        return mainPath;
    }

    private static IExtension CreateExtension(Assembly assembly, ExtensionManifest manifest)
    {
        Type? type;
        if (!string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            type = assembly.GetType(manifest.EntryType, throwOnError: false);
        }
        else
        {
            List<Type> candidates = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false }
                    && typeof(IExtension).IsAssignableFrom(t))
                .ToList();

            if (candidates.Count > 1)
                throw new AppException(
                    ErrorCode.InvalidArgument,
                    $"Plusieurs classes IExtension dans '{manifest.Id}' : précisez \"entryType\".");

            type = candidates.FirstOrDefault();
        }

        if (type is null || !typeof(IExtension).IsAssignableFrom(type))
            throw new AppException(
                ErrorCode.InvalidArgument,
                $"Aucune classe IExtension valide trouvée dans '{manifest.Id}'.");

        return (IExtension)Activator.CreateInstance(type)!;
    }

    private void EnsureCompatibleWithApp(ExtensionManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.MinAppVersion))
            return;

        Version required = Version.Parse(manifest.MinAppVersion);
        if (_appVersion < required)
        {
            throw new AppException(
                ErrorCode.Conflict,
                $"L'extension '{manifest.Id}' requiert au moins la version {manifest.MinAppVersion} de l'application (actuelle : {_appVersion}).");
        }
    }

    private void EnsureDependencies_NoLock(ExtensionManifest manifest, bool requireActive)
    {
        foreach (string dependencyId in manifest.Dependencies ?? [])
        {
            if (!_extensions.TryGetValue(dependencyId, out ExtensionInfo? dependency))
            {
                throw new AppException(
                    ErrorCode.Conflict,
                    $"L'extension '{manifest.Id}' dépend de '{dependencyId}', qui n'est pas installée.");
            }

            if (requireActive && dependency.State != ExtensionState.Active)
            {
                throw new AppException(
                    ErrorCode.Conflict,
                    $"L'extension '{manifest.Id}' dépend de '{dependencyId}', qui n'est pas activée.");
            }
        }
    }

    private void EnsureNoDependents_NoLock(
        string extensionId, bool activeOnly, string actionLabel)
    {
        foreach (ExtensionInfo other in _extensions.Values)
        {
            if (string.Equals(other.Manifest.Id, extensionId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (activeOnly && other.State != ExtensionState.Active)
                continue;

            if ((other.Manifest.Dependencies ?? []).Contains(
                extensionId, StringComparer.OrdinalIgnoreCase))
            {
                string stateText = activeOnly ? "l'extension active " : string.Empty;
                throw new AppException(
                    ErrorCode.Conflict,
                    $"Impossible de {actionLabel} '{extensionId}' car {stateText}'{other.Manifest.Id}' en dépend.");
            }
        }
    }

    private static Task<ExtensionManifest> ReadSourceManifestAsync(
        string sourceFolder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
            throw new AppException(
                ErrorCode.InvalidArgument, "Le dossier source est obligatoire.");

        string manifestPath = Path.Combine(sourceFolder, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new AppException(
                ErrorCode.NotFound,
                $"{ManifestFileName} est introuvable dans : {sourceFolder}");

        return ReadManifestAsync(manifestPath, ct);
    }

    internal static async Task<ExtensionManifest> ReadManifestAsync(
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

            if (!Version.TryParse(manifest.Version, out _))
                throw new AppException(
                    ErrorCode.InvalidArgument,
                    $"Version d'extension invalide : '{manifest.Version}'.");

            if (!string.IsNullOrWhiteSpace(manifest.MinAppVersion)
                && !Version.TryParse(manifest.MinAppVersion, out _))
            {
                throw new AppException(
                    ErrorCode.InvalidArgument,
                    $"minAppVersion invalide : '{manifest.MinAppVersion}'.");
            }

            IReadOnlyList<string> permissions = manifest.Permissions ?? [];
            foreach (string permission in permissions)
            {
                if (string.IsNullOrWhiteSpace(permission)
                    || !ExtensionPermissions.All.Contains(permission))
                {
                    throw new AppException(
                        ErrorCode.InvalidArgument,
                        $"Permission inconnue dans le manifeste : '{permission}'.");
                }
            }

            return manifest with
            {
                Description = manifest.Description ?? string.Empty,
                Permissions = permissions,
                Dependencies = manifest.Dependencies ?? []
            };
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

    private sealed record RunningExtension(
        IExtension Extension,
        ExtensionContext Context,
        ExtensionLoadContext LoadContext);
}
