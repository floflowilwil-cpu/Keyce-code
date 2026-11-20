using App.Core.Contracts;
using App.Core.Models;

namespace App.Editor.Editing;

/// <summary>
/// Commandes portant sur des lignes complètes. Les changements sont appliqués
/// via IEditorService afin de conserver l'historique Undo/Redo.
/// </summary>
public sealed class EditorLineCommandService
{
    private readonly IEditorService _editor;

    public EditorLineCommandService(IEditorService editor) =>
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

    /// <summary>Ajoute le préfixe aux lignes sélectionnées ou aux lignes des curseurs.</summary>
    public void CommentLines(DocumentId id, string prefix = "//")
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        TransformLines(id, (line, _) => prefix + line);
    }

    /// <summary>Retire le préfixe au début des lignes sélectionnées ou concernées.</summary>
    public void UncommentLines(DocumentId id, string prefix = "//")
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        TransformLines(id, (line, _) =>
            line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line);
    }

    /// <summary>Duplique chaque ligne concernée une seule fois, même avec plusieurs curseurs.</summary>
    public void DuplicateLines(DocumentId id)
    {
        var model = new TextModel(_editor.GetText(id));
        var carets = _editor.GetCaretSet(id);
        var lines = GetSelectedLines(model, carets);
        var edits = new List<TextEdit>();

        foreach (int line in lines.Order())
        {
            int start = model.GetLineStartOffset(line);
            int length = model.GetLineLength(line);
            string content = model.GetText(start, length);
            int insertion = start + length;
            string suffix = line < model.LineCount - 1 || (model.Length > 0 && model.GetText(model.Length - 1, 1) == "\n")
                ? "\n" + content
                : "\n" + content;
            edits.Add(new TextEdit(new TextRange(model.GetPosition(insertion), model.GetPosition(insertion)), suffix));
        }

        if (edits.Count > 0) _editor.ApplyEdits(id, edits);
    }

    /// <summary>Déplace vers le haut chaque ligne ciblée, en ignorant la première ligne.</summary>
    public void MoveLinesUp(DocumentId id) => MoveLines(id, -1);

    /// <summary>Déplace vers le bas chaque ligne ciblée, en ignorant la dernière ligne.</summary>
    public void MoveLinesDown(DocumentId id) => MoveLines(id, 1);

    private void TransformLines(DocumentId id, Func<string, int, string> transform)
    {
        var model = new TextModel(_editor.GetText(id));
        var lines = GetSelectedLines(model, _editor.GetCaretSet(id));
        var edits = new List<TextEdit>();
        foreach (int line in lines.Order())
        {
            string before = model.GetLineText(line);
            string after = transform(before, line);
            if (before == after) continue;
            var start = new TextPosition(line, 0);
            var end = new TextPosition(line, model.GetLineLength(line));
            edits.Add(new TextEdit(new TextRange(start, end), after));
        }
        if (edits.Count > 0) _editor.ApplyEdits(id, edits);
    }

    private void MoveLines(DocumentId id, int direction)
    {
        var model = new TextModel(_editor.GetText(id));
        var lines = GetSelectedLines(model, _editor.GetCaretSet(id));
        if (lines.Count == 0) return;

        // Les blocs contigus sont déplacés ensemble. Les blocs sont traités dans
        // l'ordre qui évite qu'un déplacement change les indices des suivants.
        var ordered = direction < 0 ? lines.Order().ToArray() : lines.OrderByDescending(x => x).ToArray();
        string[] current = Enumerable.Range(0, model.LineCount).Select(model.GetLineText).ToArray();

        foreach (int line in ordered)
        {
            int target = line + direction;
            if (target < 0 || target >= current.Length || lines.Contains(target)) continue;
            (current[line], current[target]) = (current[target], current[line]);
        }

        string updated = string.Join("\n", current);
        if (updated == model.GetText()) return;

        var whole = new TextRange(new TextPosition(0, 0), model.GetPosition(model.Length));
        _editor.ApplyEdits(id, [new TextEdit(whole, updated)]);
    }

    private static HashSet<int> GetSelectedLines(TextModel model, CaretSet carets)
    {
        var result = new HashSet<int>();
        foreach (var caret in carets.Carets)
        {
            int first = Math.Min(caret.Position.Line, caret.Anchor.Line);
            int last = Math.Max(caret.Position.Line, caret.Anchor.Line);
            // Une sélection qui se termine exactement au début d'une ligne
            // n'inclut pas cette dernière ligne.
            if (caret.HasSelection && caret.Position.Column == 0 && caret.Position.Line > caret.Anchor.Line)
                last--;
            if (caret.HasSelection && caret.Anchor.Column == 0 && caret.Anchor.Line > caret.Position.Line)
                last--;
            for (int line = Math.Max(0, first); line <= Math.Min(model.LineCount - 1, last); line++)
                result.Add(line);
        }
        return result;
    }
}
