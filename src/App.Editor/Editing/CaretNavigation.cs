using System.Collections.Concurrent;
using App.Core.Contracts;
using App.Core.Models;

namespace App.Editor.Editing;

/// <summary>
/// Déplacements de curseurs et sélections à la souris. Ne modifie jamais le texte :
/// seul le CaretSet du document change. Colonnes en UTF-16.
/// </summary>
internal sealed class CaretNavigation
{
    private readonly IEditorService _editor;

    // Colonne souhaitée mémorisée après un déplacement vertical : (position atteinte, colonne voulue).
    private readonly ConcurrentDictionary<DocumentId, List<(TextPosition Position, int Column)>> _desiredColumns = new();

    public CaretNavigation(IEditorService editor) => _editor = editor;

    // ---------- Flèches, Début/Fin, mots, pages ----------

    public void Move(DocumentId id, CaretMove move, bool extend, int pageSize)
    {
        var model = LineSelection.GetModel(_editor, id);
        var carets = _editor.GetCaretSet(id);

        bool vertical = move is CaretMove.Up or CaretMove.Down or CaretMove.PageUp or CaretMove.PageDown;
        _desiredColumns.TryGetValue(id, out var previous);
        var desiredNow = new List<(TextPosition Position, int Column)>();

        var moved = new List<Caret>(carets.Count);
        foreach (var caret in carets.Carets)
        {
            var target = Target(model, caret, move, extend, Math.Max(1, pageSize), previous, desiredNow);
            moved.Add(extend ? new Caret(target, caret.Anchor) : Caret.At(target));
        }

        if (vertical) _desiredColumns[id] = desiredNow;
        else _desiredColumns.TryRemove(id, out _);

        Commit(id, moved, carets.PrimaryIndex);
    }

    private static TextPosition Target(
        TextModel model, Caret caret, CaretMove move, bool extend, int pageSize,
        List<(TextPosition Position, int Column)>? previous,
        List<(TextPosition Position, int Column)> desiredNow)
    {
        var active = caret.Position;

        // Sans Maj, ←/→ sur une sélection la réduisent à son début / sa fin.
        if (!extend && caret.HasSelection && move is CaretMove.Left or CaretMove.Right)
        {
            bool anchorFirst = Selection.Compare(caret.Anchor, caret.Position) <= 0;
            var start = anchorFirst ? caret.Anchor : caret.Position;
            var end = anchorFirst ? caret.Position : caret.Anchor;
            return move == CaretMove.Left ? start : end;
        }

        int lineCount = model.LineCount;
        string text = model.GetLineText(active.Line);
        int col = Math.Min(active.Column, text.Length);

        switch (move)
        {
            case CaretMove.Left:
                if (col > 0)
                {
                    int c = col - 1;
                    if (c >= 1 && char.IsLowSurrogate(text[c]) && char.IsHighSurrogate(text[c - 1])) c--;
                    return new TextPosition(active.Line, c);
                }
                return active.Line > 0
                    ? new TextPosition(active.Line - 1, model.GetLineLength(active.Line - 1))
                    : new TextPosition(0, 0);

            case CaretMove.Right:
                if (col < text.Length)
                {
                    int c = col + 1;
                    if (char.IsHighSurrogate(text[col]) && c < text.Length && char.IsLowSurrogate(text[c])) c++;
                    return new TextPosition(active.Line, c);
                }
                return active.Line < lineCount - 1
                    ? new TextPosition(active.Line + 1, 0)
                    : new TextPosition(active.Line, text.Length);

            case CaretMove.Up:
            case CaretMove.Down:
            case CaretMove.PageUp:
            case CaretMove.PageDown:
            {
                int step = move switch
                {
                    CaretMove.Up => -1,
                    CaretMove.Down => 1,
                    CaretMove.PageUp => -pageSize,
                    _ => pageSize
                };
                int desired = previous?.FirstOrDefault(p => p.Position == active) is { } found && found.Position == active
                    ? found.Column
                    : col;

                int line = active.Line + step;
                TextPosition result;
                if (line < 0)
                    result = move == CaretMove.Up ? new TextPosition(0, 0) : new TextPosition(0, Math.Min(desired, model.GetLineLength(0)));
                else if (line >= lineCount)
                    result = move == CaretMove.Down
                        ? new TextPosition(lineCount - 1, model.GetLineLength(lineCount - 1))
                        : new TextPosition(lineCount - 1, Math.Min(desired, model.GetLineLength(lineCount - 1)));
                else
                    result = new TextPosition(line, Math.Min(desired, model.GetLineLength(line)));

                desiredNow.Add((result, desired));
                return result;
            }

            case CaretMove.LineStart:
            {
                int indent = 0;
                while (indent < text.Length && text[indent] is ' ' or '\t') indent++;
                return new TextPosition(active.Line, col != indent ? indent : 0);
            }

            case CaretMove.LineEnd:
                return new TextPosition(active.Line, text.Length);

            case CaretMove.DocumentStart:
                return new TextPosition(0, 0);

            case CaretMove.DocumentEnd:
                return model.GetPosition(model.Length);

            case CaretMove.WordLeft:
            {
                if (col == 0)
                    return active.Line > 0
                        ? new TextPosition(active.Line - 1, model.GetLineLength(active.Line - 1))
                        : active;
                int i = col;
                while (i > 0 && WordClass(text[i - 1]) == 0) i--;
                if (i > 0)
                {
                    int cls = WordClass(text[i - 1]);
                    while (i > 0 && WordClass(text[i - 1]) == cls) i--;
                }
                return new TextPosition(active.Line, i);
            }

            case CaretMove.WordRight:
            {
                if (col == text.Length)
                    return active.Line < lineCount - 1 ? new TextPosition(active.Line + 1, 0) : active;
                int i = col;
                while (i < text.Length && WordClass(text[i]) == 0) i++;
                if (i < text.Length)
                {
                    int cls = WordClass(text[i]);
                    while (i < text.Length && WordClass(text[i]) == cls) i++;
                }
                return new TextPosition(active.Line, i);
            }

            default:
                return active;
        }
    }

    // ---------- Souris ----------

    public void SetCaret(DocumentId id, TextPosition position, bool extend)
    {
        var model = LineSelection.GetModel(_editor, id);
        var clamped = Clamp(model, position);
        _desiredColumns.TryRemove(id, out _);

        if (extend)
        {
            var primary = _editor.GetCaretSet(id).Primary;
            _editor.SetCaretSet(id, new CaretSet([new Caret(clamped, primary.Anchor)]));
        }
        else
        {
            _editor.SetCaretSet(id, CaretSet.Single(clamped));
        }
    }

    public void AddCaret(DocumentId id, TextPosition position)
    {
        var model = LineSelection.GetModel(_editor, id);
        var clamped = Clamp(model, position);
        var carets = _editor.GetCaretSet(id);
        _desiredColumns.TryRemove(id, out _);

        int existing = carets.Carets.ToList().FindIndex(c => c.Position == clamped);
        try
        {
            if (existing >= 0)
            {
                if (carets.Count > 1) _editor.SetCaretSet(id, carets.RemoveAt(existing));
            }
            else
            {
                _editor.SetCaretSet(id, carets.Add(Caret.At(clamped)));
            }
        }
        catch (ArgumentException)
        {
            // Le clic tombe dans une sélection existante : on ne fait rien.
        }
    }

    public void SelectWord(DocumentId id, TextPosition position)
    {
        var model = LineSelection.GetModel(_editor, id);
        var p = Clamp(model, position);
        string text = model.GetLineText(p.Line);
        _desiredColumns.TryRemove(id, out _);

        if (text.Length == 0)
        {
            _editor.SetCaretSet(id, CaretSet.Single(p));
            return;
        }

        int at = p.Column < text.Length ? p.Column : text.Length - 1;
        int cls = WordClass(text[at]);
        int start = at, end = at + 1;
        while (start > 0 && WordClass(text[start - 1]) == cls) start--;
        while (end < text.Length && WordClass(text[end]) == cls) end++;

        var selection = new Selection(new TextPosition(p.Line, start), new TextPosition(p.Line, end));
        _editor.SetCaretSet(id, new CaretSet([Caret.FromSelection(selection)]));
    }

    public void SelectLine(DocumentId id, TextPosition position)
    {
        var model = LineSelection.GetModel(_editor, id);
        var p = Clamp(model, position);
        _desiredColumns.TryRemove(id, out _);

        var start = new TextPosition(p.Line, 0);
        var end = p.Line < model.LineCount - 1
            ? new TextPosition(p.Line + 1, 0)
            : new TextPosition(p.Line, model.GetLineLength(p.Line));
        _editor.SetCaretSet(id, new CaretSet([Caret.FromSelection(new Selection(start, end))]));
    }

    public void AddCaretVertically(DocumentId id, int direction)
    {
        var model = LineSelection.GetModel(_editor, id);
        var carets = _editor.GetCaretSet(id);
        _desiredColumns.TryRemove(id, out _);

        var extreme = direction < 0
            ? carets.Carets.MinBy(c => c.Position.Line)
            : carets.Carets.MaxBy(c => c.Position.Line);

        int line = extreme.Position.Line + direction;
        if (line < 0 || line >= model.LineCount) return;

        var position = new TextPosition(line, Math.Min(extreme.Position.Column, model.GetLineLength(line)));
        if (carets.Carets.Any(c => c.Position == position)) return;

        try { _editor.SetCaretSet(id, carets.Add(Caret.At(position))); }
        catch (ArgumentException) { /* tombe dans une sélection existante */ }
    }

    // ---------- Outils ----------

    private void Commit(DocumentId id, List<Caret> moved, int primaryIndex)
    {
        var primaryPosition = moved[Math.Clamp(primaryIndex, 0, moved.Count - 1)].Position;

        // Deux curseurs qui se rejoignent n'en font plus qu'un.
        var seen = new HashSet<TextPosition>();
        var unique = moved.Where(c => seen.Add(c.Position)).ToList();
        int primary = Math.Max(0, unique.FindIndex(c => c.Position == primaryPosition));

        try
        {
            _editor.SetCaretSet(id, new CaretSet(unique, primary));
        }
        catch (ArgumentException) // sélections qui se chevauchent : on ne garde que les curseurs
        {
            _editor.SetCaretSet(id, new CaretSet(unique.Select(c => Caret.At(c.Position)), primary));
        }
    }

    private static TextPosition Clamp(TextModel model, TextPosition position) =>
        model.GetPosition(model.GetOffset(position));

    /// <summary>0 : espace ; 1 : lettre, chiffre ou _ ; 2 : autre symbole.</summary>
    private static int WordClass(char c) =>
        char.IsWhiteSpace(c) ? 0 : char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;
}
