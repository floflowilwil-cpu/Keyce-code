using System;
using App.Core.Models;

namespace App.Desktop.ViewModels;

/// <summary>
/// Conversion offset du TextBox ↔ TextPosition du moteur (ligne/colonne base 0, UTF-16, texte en \n).
/// </summary>
internal static class TextPositions
{
    public static TextPosition ToPosition(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 0, lastNewLine = -1;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; lastNewLine = i; }
        }
        return new TextPosition(line, offset - lastNewLine - 1);
    }

    public static int ToOffset(string text, TextPosition position)
    {
        int index = 0, line = 0;
        while (line < position.Line)
        {
            int nl = text.IndexOf('\n', index);
            if (nl < 0) return text.Length;
            index = nl + 1;
            line++;
        }
        int lineEnd = text.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = text.Length;
        return Math.Min(index + Math.Max(0, position.Column), lineEnd);
    }
}
