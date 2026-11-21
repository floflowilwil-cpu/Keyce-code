using App.Core.Contracts;
using App.Core.Models;

namespace App.Editor.Editing;

/// <summary>
/// Implémentation de <see cref="IEditorCommands"/> : regroupe les commandes de saisie, de lignes,
/// le presse-papiers et la recherche derrière un seul contrat pour l'interface.
/// </summary>
public sealed class EditorCommands : IEditorCommands
{
    private readonly IEditorService _editor;
    private readonly EditorCommandService _commands;
    private readonly EditorLineCommandService _lines;
    private readonly SearchReplaceService _search = new();

    public EditorCommands(IEditorService editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _commands = new EditorCommandService(editor);
        _lines = new EditorLineCommandService(editor);
    }

    private sealed record Op(int Start, int End, string NewText);

    // ---- Saisie ----

    public void TypeText(DocumentId id, string text) => _commands.TypeText(id, text);
    public void Backspace(DocumentId id) => _commands.Backspace(id);
    public void DeleteForward(DocumentId id) => _commands.DeleteForward(id);
    public void InsertNewLine(DocumentId id) => _commands.InsertNewLine(id);
    public void Indent(DocumentId id, string indentation = "\t") => _commands.Indent(id, indentation);
    public void Outdent(DocumentId id) => _commands.Outdent(id);

    // ---- Lignes ----

    public void CommentLines(DocumentId id, string prefix = "//") => _lines.CommentLines(id, prefix);
    public void UncommentLines(DocumentId id, string prefix = "//") => _lines.UncommentLines(id, prefix);
    public void DuplicateLines(DocumentId id) => _lines.DuplicateLines(id);
    public void MoveLinesUp(DocumentId id) => _lines.MoveLinesUp(id);
    public void MoveLinesDown(DocumentId id) => _lines.MoveLinesDown(id);

    // ---- Sélection et presse-papiers ----

    public void SelectAll(DocumentId id)
    {
        var model = LineSelection.GetModel(_editor, id);
        var selection = new Selection(new TextPosition(0, 0), model.GetPosition(model.Length));
        _editor.SetCaretSet(id, new CaretSet([Caret.FromSelection(selection)]));
    }

    public string Copy(DocumentId id)
    {
        var model = LineSelection.GetModel(_editor, id);
        return BuildClipboardText(model, _editor.GetCaretSet(id));
    }

    public string Cut(DocumentId id)
    {
        var model = LineSelection.GetModel(_editor, id);
        var carets = _editor.GetCaretSet(id);
        string text = BuildClipboardText(model, carets);

        var selections = SelectionRanges(model, carets);
        var deletions = selections.Count > 0
            ? selections
            : LineRuns(model, LineSelection.GetLines(model, carets));

        ApplyOperations(id, model, deletions.Select(d => new Op(d.Start, d.End, string.Empty)).ToList(), caretAfterText: false);
        return text;
    }

    public void Paste(DocumentId id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length == 0) return;

        var model = LineSelection.GetModel(_editor, id);
        var carets = _editor.GetCaretSet(id);
        var ranges = carets.Carets.Select(c => RangeOf(model, c)).OrderBy(r => r.Start).ToList();

        string[]? perCaret = null;
        if (ranges.Count > 1)
        {
            string trimmed = text.EndsWith('\n') ? text[..^1] : text;
            var parts = trimmed.Split('\n');
            if (parts.Length == ranges.Count) perCaret = parts;
        }

        var ops = ranges.Select((r, i) => new Op(r.Start, r.End, perCaret?[i] ?? text)).ToList();
        ApplyOperations(id, model, ops, caretAfterText: true);
    }

    // ---- Recherche ----

    public IReadOnlyList<TextRange> FindAll(DocumentId id, string pattern, SearchOptions? options = null)
    {
        var model = LineSelection.GetModel(_editor, id);
        return _search.FindAll(model, pattern, options)
            .Select(m => ToRange(model, m))
            .ToList();
    }

    public TextRange? FindNext(DocumentId id, string pattern, TextPosition from, SearchOptions? options = null)
    {
        var model = LineSelection.GetModel(_editor, id);
        var match = _search.FindNext(model, pattern, model.GetOffset(from), options);
        return match is null ? null : ToRange(model, match);
    }

    public TextRange? FindPrevious(DocumentId id, string pattern, TextPosition from, SearchOptions? options = null)
    {
        var model = LineSelection.GetModel(_editor, id);
        var match = _search.FindPrevious(model, pattern, model.GetOffset(from), options);
        return match is null ? null : ToRange(model, match);
    }

    public bool ReplaceNext(DocumentId id, string pattern, string replacement, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(replacement);

        var model = LineSelection.GetModel(_editor, id);
        var primary = _editor.GetCaretSet(id).Primary;
        int from = Math.Min(model.GetOffset(primary.Position), model.GetOffset(primary.Anchor));

        var match = _search.FindNext(model, pattern, from, options);
        if (match is null) return false;

        var edit = _search.CreateReplaceEdit(model, match, replacement, options);
        if (edit is null) return false;

        int newEnd = match.Offset + TextModel.NormalizeNewLines(edit.NewText).Length;
        _editor.ApplyEdits(id, [edit]);

        var after = LineSelection.GetModel(_editor, id);
        var next = _search.FindNext(after, pattern, newEnd, options);
        var caret = next is null
            ? Caret.At(after.GetPosition(newEnd))
            : Caret.FromSelection(new Selection(
                after.GetPosition(next.Offset),
                after.GetPosition(next.Offset + next.Length)));
        _editor.SetCaretSet(id, new CaretSet([caret]));
        return true;
    }

    public int ReplaceAll(DocumentId id, string pattern, string replacement, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(replacement);

        var model = LineSelection.GetModel(_editor, id);
        var edits = _search.CreateReplaceAllEdits(model, pattern, replacement, options);
        if (edits.Count == 0) return 0;

        _editor.ApplyEdits(id, edits);
        return edits.Count;
    }

    // ---- Outils ----

    private static TextRange ToRange(TextModel model, SearchMatch match) =>
        new(model.GetPosition(match.Offset), model.GetPosition(match.Offset + match.Length));

    private static (int Start, int End) RangeOf(TextModel model, Caret caret)
    {
        int a = model.GetOffset(caret.Position);
        int b = model.GetOffset(caret.Anchor);
        return (Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>Plages des curseurs qui ont une sélection, dans l'ordre du document.</summary>
    private static List<(int Start, int End)> SelectionRanges(TextModel model, CaretSet carets) =>
        carets.Carets
            .Where(c => c.HasSelection)
            .Select(c => RangeOf(model, c))
            .Distinct()
            .OrderBy(r => r.Start)
            .ToList();

    private static string BuildClipboardText(TextModel model, CaretSet carets)
    {
        var selections = SelectionRanges(model, carets);
        if (selections.Count > 0)
            return string.Join("\n", selections.Select(r => model.GetText(r.Start, r.End - r.Start)));

        var lines = LineSelection.GetLines(model, carets).Order().Select(model.GetLineText);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Plages à supprimer pour retirer des lignes entières, par blocs contigus.</summary>
    private static List<(int Start, int End)> LineRuns(TextModel model, IEnumerable<int> lines)
    {
        var result = new List<(int Start, int End)>();
        foreach (var (first, last) in LineSelection.ToRuns(lines))
        {
            if (last + 1 < model.LineCount)
                result.Add((model.GetLineStartOffset(first), model.GetLineStartOffset(last + 1)));
            else if (first > 0) // dernières lignes : on retire aussi le saut de ligne qui les précède
                result.Add((model.GetLineStartOffset(first - 1) + model.GetLineLength(first - 1), model.Length));
            else
                result.Add((0, model.Length));
        }
        return result;
    }

    /// <summary>
    /// Applique des opérations non chevauchantes (offsets du texte actuel) en une étape Undo,
    /// puis place un curseur par opération (au début, ou après le texte inséré).
    /// </summary>
    private void ApplyOperations(DocumentId id, TextModel model, List<Op> operations, bool caretAfterText)
    {
        var ordered = operations.OrderBy(o => o.Start).ToList();
        var edits = ordered
            .Select(o => new TextEdit(new TextRange(model.GetPosition(o.Start), model.GetPosition(o.End)), o.NewText))
            .ToArray();
        int primary = _editor.GetCaretSet(id).PrimaryIndex;

        _editor.ApplyEdits(id, edits);

        var after = LineSelection.GetModel(_editor, id);
        var carets = new List<Caret>(ordered.Count);
        var seen = new HashSet<TextPosition>();
        int shift = 0;
        foreach (var o in ordered)
        {
            int offset = o.Start + shift + (caretAfterText ? o.NewText.Length : 0);
            var position = after.GetPosition(Math.Min(offset, after.Length));
            if (seen.Add(position)) carets.Add(Caret.At(position)); // deux suppressions voisines peuvent se rejoindre
            shift += o.NewText.Length - (o.End - o.Start);
        }

        if (carets.Count > 0)
            _editor.SetCaretSet(id, new CaretSet(carets, Math.Clamp(primary, 0, carets.Count - 1)));
    }
}
