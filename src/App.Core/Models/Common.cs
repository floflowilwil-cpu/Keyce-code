namespace App.Core.Models;

/// <summary>Position dans un texte (ligne et colonne en base 0).</summary>
public readonly record struct TextPosition(int Line, int Column);

public readonly record struct TextRange(TextPosition Start, TextPosition End);

public sealed record TextEdit(TextRange Range, string NewText);

public sealed record SourceLocation(string Path, TextRange Range);
