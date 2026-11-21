using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>
/// Service de fichiers qui détecte et conserve l'encodage. Optionnel : un <see cref="IFileService"/> qui
/// n'implémente pas cette interface est traité comme du UTF-8 sans BOM.
/// </summary>
public interface IEncodedFileService : IFileService
{
    /// <summary>
    /// Lit le fichier et détecte l'encodage : BOM (UTF-8, UTF-16 LE/BE) ; sans BOM, UTF-8 s'il est valide,
    /// sinon Windows 1252.
    /// </summary>
    Task<FileContent> ReadWithEncodingAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Écrit le texte avec l'encodage donné (BOM compris). Lève InvalidArgument si le texte contient des
    /// caractères que l'encodage ne peut pas représenter : on n'écrit jamais de « ? » à la place.
    /// </summary>
    Task WriteAsync(string path, string content, FileEncoding encoding, CancellationToken ct = default);
}
