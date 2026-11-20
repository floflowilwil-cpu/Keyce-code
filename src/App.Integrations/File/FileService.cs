using System.Collections.Concurrent;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Integrations.Files;

/// <summary>
/// Implémentation de <see cref="IFileService"/> sur le système de fichiers local.
///
/// Encodage : lecture classique (<see cref="File.ReadAllTextAsync(string, CancellationToken)"/> : UTF-8 par défaut,
/// BOM Unicode reconnu et retiré) et écriture en UTF-8 sans BOM. La détection d'autres encodages
/// (Latin-1, conservation du BOM…) est volontairement reportée.
///
/// Erreurs : les erreurs d'E/S sont converties en <see cref="AppException"/> (NotFound, AccessDenied,
/// AlreadyExists, InvalidArgument). L'annulation lève <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class FileService : IFileService
{
    /// <summary>Les doublons de « Changed » plus rapprochés que cette durée pour un même chemin sont ignorés.</summary>
    private static readonly TimeSpan ChangedDebounce = TimeSpan.FromMilliseconds(100);

    public async Task<string> ReadAsync(string path, CancellationToken ct = default)
    {
        string full = Resolve(path);
        try
        {
            return await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex, full);
        }
    }

    public async Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        string full = Resolve(path);
        try
        {
            await File.WriteAllTextAsync(full, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex, full);
        }
    }

    public Task CreateAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string full = Resolve(path);
        try
        {
            if (File.Exists(full) || Directory.Exists(full))
                throw new AppException(ErrorCode.AlreadyExists, $"Existe déjà : {full}");

            if (isDirectory)
            {
                Directory.CreateDirectory(full);
            }
            else
            {
                // CreateNew échoue si le fichier apparaît entre-temps (pas d'écrasement possible).
                using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
            }
            return Task.CompletedTask;
        }
        catch (IOException) when (File.Exists(full) || Directory.Exists(full))
        {
            // Créé par un autre processus entre la vérification et CreateNew.
            throw new AppException(ErrorCode.AlreadyExists, $"Existe déjà : {full}");
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw Translate(ex, full);
        }
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string full = Resolve(path);

        // Garde-fou : on ne supprime jamais une racine de disque (ex. « / » ou « C:\ »).
        string trimmed = Path.TrimEndingDirectorySeparator(full);
        if (trimmed.Length == 0 || Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? "") == trimmed)
            throw new AppException(ErrorCode.InvalidArgument, $"Suppression refusée (racine du système de fichiers) : {full}");

        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else if (File.Exists(full)) File.Delete(full);
            else throw new AppException(ErrorCode.NotFound, $"Introuvable : {full}");
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw Translate(ex, full);
        }
    }

    public IDisposable WatchChanges(string path, Action<FileChange> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        string full = Resolve(path);

        bool isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
            throw new AppException(ErrorCode.NotFound, $"Introuvable : {full}");

        // Un fichier est surveillé via son dossier parent, filtré sur son nom.
        string directory = isDirectory ? full : Path.GetDirectoryName(full)!;
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = isDirectory,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        if (!isDirectory) watcher.Filter = Path.GetFileName(full);

        var lastChanged = new ConcurrentDictionary<string, long>();

        void Emit(FileChange change)
        {
            try { onChange(change); }
            catch { /* une erreur du consommateur ne doit pas faire tomber le processus */ }
        }

        watcher.Created += (_, e) => Emit(new FileChange(e.FullPath, FileChangeKind.Created));
        watcher.Deleted += (_, e) => Emit(new FileChange(e.FullPath, FileChangeKind.Deleted));
        watcher.Renamed += (_, e) => Emit(new FileChange(e.FullPath, FileChangeKind.Renamed, e.OldFullPath));
        watcher.Changed += (_, e) =>
        {
            long now = Environment.TickCount64;
            long last = lastChanged.GetOrAdd(e.FullPath, 0);
            if (now - last < ChangedDebounce.TotalMilliseconds) return;
            lastChanged[e.FullPath] = now;
            Emit(new FileChange(e.FullPath, FileChangeKind.Changed));
        };

        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AppException(ErrorCode.InvalidArgument, "Le chemin est vide.");
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AppException(ErrorCode.InvalidArgument, $"Chemin invalide : {path}", ex);
        }
    }

    private static AppException Translate(Exception ex, string path) => ex switch
    {
        AppException app => app,
        FileNotFoundException or DirectoryNotFoundException =>
            new AppException(ErrorCode.NotFound, $"Introuvable : {path}", ex),
        UnauthorizedAccessException =>
            new AppException(ErrorCode.AccessDenied, $"Accès refusé : {path}", ex),
        PathTooLongException or ArgumentException or NotSupportedException =>
            new AppException(ErrorCode.InvalidArgument, $"Chemin invalide : {path}", ex),
        _ => new AppException(ErrorCode.Unknown, $"Erreur d'E/S sur {path} : {ex.Message}", ex),
    };
}
