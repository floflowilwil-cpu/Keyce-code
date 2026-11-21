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

    /// <summary>Ajoute le préfixe (après l'indentation) aux lignes sélectionnées ou aux lignes des curseurs.</summary>
    public void CommentLines(DocumentId id, string prefix = "//")
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        // Le préfixe est placé après l'indentation de la ligne : l'indentation reste alignée et
        // UncommentLines retrouve exactement le texte d'origine.
        TransformLines(id, (line, _) =>
        {
            int indent = 0;
            while (indent < line.Length && line[indent] is ' ' or '\t') indent++;
            return line[..indent] + prefix + line[indent..];
        });
    }

    /// <summary>Retire le préfixe (et un espace qui le suit) des lignes sélectionnées ou concernées, après leur indentation.</summary>
    public void UncommentLines(DocumentId id, string prefix = "//")
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        TransformLines(id, (line, _) =>
        {
            // Le préfixe peut suivre l'indentation ; un espace unique après le préfixe est retiré avec lui.
            int indent = 0;
            while (indent < line.Length && line[indent] is ' ' or '\t') indent++;
            if (!line.AsSpan(indent).StartsWith(prefix, StringComparison.Ordinal)) return line;

            int after = indent + prefix.Length;
            if (after < line.Length && line[after] == ' ') after++;
            return line[..indent] + line[after..];
        });
    }

    /// <summary>Duplique chaque ligne concernée une seule fois, même avec plusieurs curseurs.</summary>
    public void DuplicateLines(DocumentId id)
    {
        var model = LineSelection.GetModel(_editor, id);
        var carets = _editor.GetCaretSet(id);
        var lines = LineSelection.GetLines(model, carets);
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
        var model = LineSelection.GetModel(_editor, id);
        var lines = LineSelection.GetLines(model, _editor.GetCaretSet(id));
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
        var model = LineSelection.GetModel(_editor, id);
        var carets = _editor.GetCaretSet(id);
        var lines = LineSelection.GetLines(model, carets);
        if (lines.Count == 0) return;

        // Chaque bloc de lignes contiguës échange sa place avec la ligne voisine : une seule édition par bloc,
        // limitée aux lignes concernées (le reste du document n'est pas réécrit).
        var edits = new List<TextEdit>();
        var moved = new List<(int Start, int End)>();
        foreach (var (start, end) in LineSelection.ToRuns(lines))
        {
            if (direction < 0 ? start == 0 : end == model.LineCount - 1) continue;

            int from = direction < 0 ? start - 1 : start;
            int to = direction < 0 ? end : end + 1;
            string neighbor = model.GetLineText(direction < 0 ? start - 1 : end + 1);
            string block = string.Join("\n", Enumerable.Range(start, end - start + 1).Select(model.GetLineText));
            string replacement = direction < 0 ? block + "\n" + neighbor : neighbor + "\n" + block;

            edits.Add(new TextEdit(
                new TextRange(new TextPosition(from, 0), new TextPosition(to, model.GetLineLength(to))),
                replacement));
            moved.Add((start, end));
        }
        if (edits.Count == 0) return;

        var shifted = carets.Carets.Select(c =>
        {
            var boundary = LineSelection.GetBoundaryEnd(c);
            return new Caret(
                ShiftLine(c.Position, c.Position == boundary, moved, direction),
                ShiftLine(c.Anchor, c.Anchor == boundary, moved, direction));
        }).ToList();

        _editor.ApplyEdits(id, edits);
        _editor.SetCaretSet(id, new CaretSet(shifted, carets.PrimaryIndex));
    }

    /// <summary>Les curseurs suivent les lignes déplacées (y compris la fin de sélection en colonne 0).</summary>
    private static TextPosition ShiftLine(
        TextPosition position, bool isBoundary, List<(int Start, int End)> moved, int direction)
    {
        foreach (var (start, end) in moved)
        {
            if (position.Line >= start && position.Line <= end)
                return new TextPosition(position.Line + direction, position.Column);
            if (isBoundary && position.Line == end + 1 && position.Column == 0)
                return new TextPosition(position.Line + direction, 0);
        }
        return position;
    }
}
