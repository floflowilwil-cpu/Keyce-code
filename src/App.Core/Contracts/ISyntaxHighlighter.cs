using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Coloration syntaxique. Utilisé par le contrôle d'édition.</summary>
public interface ISyntaxHighlighter
{
    void OpenDocument(string path, string languageId, string text);
    void UpdateDocument(string path, string newText);
    void CloseDocument(string path);

    /// <summary>Jetons d'une ligne (base 0), triés par colonne. Vide si la ligne n'a rien à colorer.</summary>
    IReadOnlyList<SyntaxToken> GetTokens(string path, int line);

    /// <summary>Des lignes ont changé de couleur : le contrôle doit les redessiner.</summary>
    event EventHandler<TokensInvalidatedEventArgs> TokensInvalidated;
}