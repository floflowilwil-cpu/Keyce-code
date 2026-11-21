namespace App.Core.Models;

/// <summary>Catégorie d'un jeton. Le contrôle associe une couleur à chaque catégorie (thèmes).</summary>
public enum TokenKind
{
    Default,

    // Langages de programmation
    Keyword, Type, Function, Variable, Property, Parameter, Constant, Namespace,
    Preprocessor, Operator, Punctuation,
    String, StringEscape, Number, Comment,

    // HTML / XML
    Tag, Attribute,

    // Markdown
    Heading, Bold, Italic, Link, Code,

    // Code invalide
    Error
}

/// <summary>Une plage colorée sur une ligne. Colonnes en base 0, UTF-16, fin exclue.</summary>
public readonly record struct SyntaxToken(int StartColumn, int EndColumn, TokenKind Kind);

/// <summary>Les lignes FirstLine à LastLine (incluses, base 0) ont changé de couleur.</summary>
public sealed class TokensInvalidatedEventArgs(DocumentId documentId, int firstLine, int lastLine) : EventArgs
{
    public DocumentId DocumentId { get; } = documentId;
    public int FirstLine { get; } = firstLine;
    public int LastLine { get; } = lastLine;
}