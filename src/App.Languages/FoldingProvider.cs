using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Languages;

/// <summary>Calcule les plages repliables d'après l'indentation du texte.</summary>
public static class IndentationFolding
{
    public static IReadOnlyList<FoldingRange> Compute(string text, int tabSize = 4)
    {
        var lines = text.Split('\n');
        var ranges = new List<FoldingRange>();
        var stack = new Stack<(int Line, int Indent)>();
        var previous = -1;   // dernière ligne non vide

        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            var indent = IndentOf(lines[i], tabSize);

            // Les lignes moins ou aussi indentées ferment les blocs ouverts avant elles.
            while (stack.Count > 0 && stack.Peek().Indent >= indent)
            {
                var (start, _) = stack.Pop();
                if (previous > start) ranges.Add(new FoldingRange(start, previous));
            }

            stack.Push((i, indent));
            previous = i;
        }

        while (stack.Count > 0)
        {
            var (start, _) = stack.Pop();
            if (previous > start) ranges.Add(new FoldingRange(start, previous));
        }

        return ranges.OrderBy(r => r.StartLine).ThenBy(r => r.EndLine).ToList();
    }

    private static int IndentOf(string line, int tabSize)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += tabSize - width % tabSize;
            else break;
        }
        return width;
    }
}

/// <summary>Fournit les plages repliables des documents de l'éditeur.</summary>
public sealed class FoldingProvider(IEditorService editor) : IFoldingProvider
{
    public IReadOnlyList<FoldingRange> GetFoldingRanges(DocumentId documentId)
    {
        try
        {
            return IndentationFolding.Compute(editor.GetText(documentId));
        }
        catch (AppException)
        {
            return [];   // document fermé ou inconnu
        }
    }
}