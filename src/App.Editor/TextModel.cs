using App.Core.Errors;
using App.Core.Models;

namespace App.Editor;

/// <summary>
/// Texte d'un document avec lignes et colonnes : s'appuie sur <see cref="TextBuffer"/> (piece table, par
/// décalages) et ajoute un index des débuts de ligne mis à jour de façon incrémentale.
/// Le texte interne n'utilise que '\n' ; toute entrée est normalisée (\r\n et \r deviennent \n).
/// Lignes et colonnes sont en base 0 ; les colonnes comptent des unités UTF-16 (comme le LSP).
/// Non thread-safe : l'appelant (Document / EditorService) sérialise les accès.
/// </summary>
public sealed class TextModel
{
    private readonly TextBuffer _buffer;
    private readonly List<int> _lineStarts = new() { 0 };

    public TextModel(string text = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        text = NormalizeNewLines(text);
        _buffer = new TextBuffer(text);
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') _lineStarts.Add(i + 1);
    }

    public int Length => _buffer.Length;
    public int LineCount => _lineStarts.Count;

    public static string NormalizeNewLines(string text) =>
        text.IndexOf('\r') < 0 ? text : text.Replace("\r\n", "\n").Replace('\r', '\n');

    public string GetText() => _buffer.GetText();

    public string GetText(int offset, int length)
    {
        CheckRange(offset, length);
        return _buffer.GetText(offset, length);
    }

    public string GetText(TextRange range)
    {
        int start = GetOffset(range.Start), end = GetOffset(range.End);
        if (end < start)
            throw new AppException(ErrorCode.InvalidArgument, "La fin de la plage précède son début.");
        return _buffer.GetText(start, end - start);
    }

    public int GetLineStartOffset(int line)
    {
        CheckLine(line);
        return _lineStarts[line];
    }

    /// <summary>Longueur de la ligne sans le saut de ligne final.</summary>
    public int GetLineLength(int line)
    {
        CheckLine(line);
        int end = line + 1 < _lineStarts.Count ? _lineStarts[line + 1] - 1 : Length;
        return end - _lineStarts[line];
    }

    public string GetLineText(int line) => _buffer.GetText(GetLineStartOffset(line), GetLineLength(line));

    /// <summary>
    /// Convertit une position en décalage. Comme le prévoit le LSP : une colonne trop grande est ramenée
    /// à la fin de la ligne, une ligne trop grande à la fin du document. Une valeur négative est une erreur.
    /// </summary>
    public int GetOffset(TextPosition position)
    {
        if (position.Line < 0 || position.Column < 0)
            throw new AppException(ErrorCode.InvalidArgument, $"Position négative : {position}.");
        if (position.Line >= _lineStarts.Count) return Length;
        return _lineStarts[position.Line] + Math.Min(position.Column, GetLineLength(position.Line));
    }

    public TextPosition GetPosition(int offset)
    {
        if (offset < 0 || offset > Length)
            throw new AppException(ErrorCode.InvalidArgument, $"Décalage hors limites : {offset}.");
        int line = LineIndexOf(offset);
        return new TextPosition(line, offset - _lineStarts[line]);
    }

    public void Insert(int offset, string text) => Replace(offset, 0, text);
    public void Delete(int offset, int length) => Replace(offset, length, string.Empty);

    public void Replace(int offset, int length, string newText)
    {
        ArgumentNullException.ThrowIfNull(newText);
        CheckRange(offset, length);
        newText = NormalizeNewLines(newText);
        if (length > 0) DeleteCore(offset, length);
        if (newText.Length > 0) InsertCore(offset, newText);
    }

    private void InsertCore(int offset, string text)
    {
        int line = LineIndexOf(offset);
        _buffer.Insert(offset, text);
        for (int i = line + 1; i < _lineStarts.Count; i++) _lineStarts[i] += text.Length;

        List<int>? added = null;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') (added ??= new()).Add(offset + i + 1);
        if (added != null) _lineStarts.InsertRange(line + 1, added);
    }

    private void DeleteCore(int offset, int length)
    {
        int end = offset + length;
        int first = LineIndexOf(offset) + 1;
        _buffer.Delete(offset, length);

        int removed = 0;
        while (first + removed < _lineStarts.Count && _lineStarts[first + removed] <= end) removed++;
        if (removed > 0) _lineStarts.RemoveRange(first, removed);
        for (int i = first; i < _lineStarts.Count; i++) _lineStarts[i] -= length;
    }

    private int LineIndexOf(int offset)
    {
        int idx = _lineStarts.BinarySearch(offset);
        return idx >= 0 ? idx : ~idx - 1;
    }

    private void CheckRange(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > Length)
            throw new AppException(ErrorCode.InvalidArgument, $"Plage hors limites : {offset}+{length} (longueur {Length}).");
    }

    private void CheckLine(int line)
    {
        if (line < 0 || line >= _lineStarts.Count)
            throw new AppException(ErrorCode.InvalidArgument, $"Ligne hors limites : {line} (total {_lineStarts.Count}).");
    }
}
