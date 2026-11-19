using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Accès au système de fichiers. Utilisé par UI, éditeur, extensions.</summary>
public interface IFileService
{
    Task<string> ReadAsync(string path, CancellationToken ct = default);
    Task WriteAsync(string path, string content, CancellationToken ct = default);
    Task CreateAsync(string path, bool isDirectory, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Surveille un dossier ou un fichier. Disposer le résultat arrête la surveillance.</summary>
    IDisposable WatchChanges(string path, Action<FileChange> onChange);
}
