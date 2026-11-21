using App.Core.Contracts;
using App.Core.Models;

namespace App.Editor.Editing;

/// <summary>Outils partagés par les commandes de lignes et d'indentation.</summary>
internal static class LineSelection
{
    /// <summary>Modèle du document : accès direct si disponible, sinon copie du texte.</summary>
    public static TextModel GetModel(IEditorService editor, DocumentId id) =>
        editor is ITextModelSource source ? source.GetTextModel(id) : new TextModel(editor.GetText(id));

    /// <summary>
    /// Lignes concernées par les curseurs. Une sélection qui se termine exactement au début
    /// d'une ligne n'inclut pas cette dernière ligne.
    /// </summary>
    public static HashSet<int> GetLines(TextModel model, CaretSet carets)
    {
        var result = new HashSet<int>();
        foreach (var caret in carets.Carets)
        {
            int first = Math.Min(caret.Position.Line, caret.Anchor.Line);
            int last = Math.Max(caret.Position.Line, caret.Anchor.Line);
            if (GetBoundaryEnd(caret) is not null) last--;
            for (int line = Math.Max(0, first); line <= Math.Min(model.LineCount - 1, last); line++)
                result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Extrémité d'une sélection multi-lignes qui se trouve en colonne 0 (la ligne de cette extrémité
    /// n'est pas comptée comme sélectionnée), ou null.
    /// </summary>
    public static TextPosition? GetBoundaryEnd(Caret caret)
    {
        if (!caret.HasSelection) return null;
        var end = Selection.Compare(caret.Anchor, caret.Position) <= 0 ? caret.Position : caret.Anchor;
        var start = end == caret.Position ? caret.Anchor : caret.Position;
        return end.Column == 0 && end.Line > start.Line ? end : null;
    }

    /// <summary>Découpe des numéros de lignes triés en blocs contigus.</summary>
    public static List<(int Start, int End)> ToRuns(IEnumerable<int> lines)
    {
        var runs = new List<(int Start, int End)>();
        foreach (int line in lines.Order())
        {
            if (runs.Count > 0 && runs[^1].End == line - 1) runs[^1] = (runs[^1].Start, line);
            else runs.Add((line, line));
        }
        return runs;
    }
}
