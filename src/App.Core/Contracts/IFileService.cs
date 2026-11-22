using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Accès au système de fichiers. Utilisé par UI, éditeur, extensions.</summary>
public interface IFileService
{
    Task<string> ReadAsync(string path, CancellationToken ct = default);
    Task WriteAsync(string path, string content, CancellationToken ct = default);
    Task CreateAsync(string path, bool isDirectory, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Renomme ou déplace un fichier ou un dossier (<paramref name="newPath"/> est le chemin complet de destination).
    /// Erreurs : NotFound (source absente ou dossier de destination absent), AlreadyExists (destination prise),
    /// InvalidArgument (déplacer un dossier dans lui-même). Un changement de casse seul est accepté.
    /// Méthode par défaut : les doublures de test existantes continuent de compiler.
    /// </summary>
    Task MoveAsync(string path, string newPath, CancellationToken ct = default) =>
        throw new NotSupportedException("Ce service de fichiers ne sait pas renommer.");

    /// <summary>Surveille un dossier ou un fichier. Disposer le résultat arrête la surveillance.</summary>
    IDisposable WatchChanges(string path, Action<FileChange> onChange);
}
