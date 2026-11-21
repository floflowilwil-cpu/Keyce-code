using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>
/// Coloration syntaxique. Utilisé par le contrôle d'édition.
/// L'implémentation suit les documents d'elle-même, via IEditorService
/// (DocumentChanged, DocumentStateChanged, GetOpenDocuments) : personne n'a à l'appeler.
/// Lignes en base 0, colonnes en UTF-16, texte en \n (comme TextModel).
/// </summary>
public interface ISyntaxHighlighter
{
    /// <summary>Jetons d'une ligne, triés par colonne. Vide si rien à colorer ou document inconnu.</summary>
    IReadOnlyList<SyntaxToken> GetTokens(DocumentId documentId, int line);

    /// <summary>Des lignes ont changé de couleur : le contrôle doit les redessiner.</summary>
    event EventHandler<TokensInvalidatedEventArgs> TokensInvalidated;
}