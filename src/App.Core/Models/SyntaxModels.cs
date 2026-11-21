namespace App.Core.Models;

public enum TokenKind
{
    Default, Keyword, String, Comment, Number,
    Type, Function, Variable, Constant, Operator, Punctuation
}

/// <summary>Une plage colorée sur une ligne. Colonnes en base 0, fin exclue.</summary>
public readonly record struct SyntaxToken(int StartColumn, int EndColumn, TokenKind Kind);

public sealed class TokensInvalidatedEventArgs(string path, int firstLine, int lastLine) : EventArgs
{
    public string Path { get; } = path;
    public int FirstLine { get; } = firstLine;
    public int LastLine { get; } = lastLine;
}