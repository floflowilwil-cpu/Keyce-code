using App.Core.Contracts;
using App.Core.Models;

namespace App.Editor.Editing;

/// <summary>
/// Traduit les actions clavier en TextEdit. Le service ne modifie jamais le tampon
/// directement : toutes les mutations passent par IEditorService et son historique.
/// </summary>
public sealed class EditorCommandService
{
    private const int IndentWidth = 4;
    private readonly IEditorService _editor;

    public EditorCommandService(IEditorService editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    /// <summary>Insère le texte à chaque curseur, en remplaçant les sélections.</summary>
    public void TypeText(DocumentId documentId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return;
        Execute(documentId, (model, caret, start, end) =>
            new PlannedOperation(start, end, text));
    }

    /// <summary>Supprime la sélection ou le caractère Unicode précédent.</summary>
    public void Backspace(DocumentId documentId)
    {
        Execute(documentId, (model, caret, start, end) =>
        {
            if (start != end) return new PlannedOperation(start, end, string.Empty);
            if (start == 0) return null;
            int previous = PreviousTextElementStart(model, start);
            return new PlannedOperation(previous, start, string.Empty);
        });
    }

    /// <summary>Supprime la sélection ou le caractère Unicode suivant.</summary>
    public void DeleteForward(DocumentId documentId)
    {
        Execute(documentId, (model, caret, start, end) =>
        {
            if (start != end) return new PlannedOperation(start, end, string.Empty);
            if (end == model.Length) return null;
            int next = NextTextElementEnd(model, end);
            return new PlannedOperation(end, next, string.Empty);
        });
    }

    /// <summary>
    /// Insère un saut de ligne et reprend l'indentation initiale de la ligne.
    /// Le texte interne du document utilise toujours LF.
    /// </summary>
    public void InsertNewLine(DocumentId documentId)
    {
        Execute(documentId, (model, caret, start, end) =>
        {
            string indentation = GetLeadingWhitespace(model.GetLineText(caret.Position.Line));
            return new PlannedOperation(start, end, "\n" + indentation);
        });
    }

    /// <summary>Insère une tabulation à chaque curseur, en remplaçant les sélections.</summary>
    public void Indent(DocumentId documentId, string indentation = "\t")
    {
        ArgumentException.ThrowIfNullOrEmpty(indentation);
        if (indentation.Contains('\r') || indentation.Contains('\n'))
            throw new ArgumentException("L'indentation ne peut pas contenir de saut de ligne.", nameof(indentation));

        Execute(documentId, (model, caret, start, end) =>
            new PlannedOperation(start, end, indentation));
    }

    /// <summary>
    /// Retire une tabulation ou jusqu'à quatre espaces au début de chaque ligne
    /// concernée. Plusieurs curseurs sur une même ligne ne produisent qu'une édition.
    /// </summary>
    public void Outdent(DocumentId documentId)
    {
        string text = _editor.GetText(documentId);
        var model = new TextModel(text);
        var carets = _editor.GetCaretSet(documentId);
        var lineNumbers = carets.Carets
            .Select(c => c.Position.Line)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var edits = new List<TextEdit>();
        foreach (int line in lineNumbers)
        {
            string lineText = model.GetLineText(line);
            int remove = lineText.StartsWith('\t') ? 1 :
                Math.Min(IndentWidth, CountLeadingSpaces(lineText));
            if (remove == 0) continue;

            var start = new TextPosition(line, 0);
            var end = new TextPosition(line, remove);
            edits.Add(new TextEdit(new TextRange(start, end), string.Empty));
        }

        if (edits.Count == 0) return;
        _editor.ApplyEdits(documentId, edits);

        var updated = new List<Caret>(carets.Count);
        foreach (var caret in carets.Carets)
        {
            int removedBefore = lineNumbers
                .Where(line => line < caret.Position.Line)
                .Sum(line =>
                {
                    string lineText = model.GetLineText(line);
                    return lineText.StartsWith('\t') ? 1 :
                        Math.Min(IndentWidth, CountLeadingSpaces(lineText));
                });

            int removedOnLine = lineNumbers.Contains(caret.Position.Line)
                ? (model.GetLineText(caret.Position.Line).StartsWith('\t') ? 1 :
                    Math.Min(IndentWidth, CountLeadingSpaces(model.GetLineText(caret.Position.Line))))
                : 0;

            var position = new TextPosition(
                caret.Position.Line,
                Math.Max(0, caret.Position.Column - removedOnLine));
            // Les suppressions d'indentation ne changent pas le nombre de lignes.
            updated.Add(new Caret(position, new TextPosition(
                caret.Anchor.Line,
                Math.Max(0, caret.Anchor.Column -
                    (caret.Anchor.Line == caret.Position.Line ? removedOnLine : 0)))));
        }

        _editor.SetCaretSet(documentId, new CaretSet(updated, carets.PrimaryIndex));
    }

    private void Execute(
        DocumentId documentId,
        Func<TextModel, Caret, int, int, PlannedOperation?> createOperation)
    {
        string originalText = _editor.GetText(documentId);
        var model = new TextModel(originalText);
        var caretSet = _editor.GetCaretSet(documentId);

        var plans = new List<CaretPlan>(caretSet.Count);
        var operations = new List<PlannedOperation>(caretSet.Count);

        foreach (var caret in caretSet.Carets)
        {
            int activeOffset = model.GetOffset(caret.Position);
            int anchorOffset = model.GetOffset(caret.Anchor);
            int start = Math.Min(activeOffset, anchorOffset);
            int end = Math.Max(activeOffset, anchorOffset);

            var operation = createOperation(model, caret, start, end);
            if (operation is not null)
            {
                operations.Add(operation);
                plans.Add(new CaretPlan(operation.Start, operation.NewText.Length, operation));
            }
            else
            {
                plans.Add(new CaretPlan(activeOffset, 0, null));
            }
        }

        // Les opérations identiques peuvent apparaître lorsqu'une commande agit
        // sur une même ligne. Elles ne doivent être appliquées qu'une seule fois.
        operations = operations
            .DistinctBy(o => (o.Start, o.End, o.NewText))
            .ToList();

        if (operations.Count > 0)
        {
            var edits = operations
                .Select(o => new TextEdit(
                    new TextRange(model.GetPosition(o.Start), model.GetPosition(o.End)),
                    o.NewText))
                .ToArray();
            _editor.ApplyEdits(documentId, edits);
        }

        var resultingCarets = new List<Caret>(plans.Count);
        foreach (var plan in plans)
        {
            int shift = operations
                .Where(o => o.Start < plan.OriginalStart)
                .Sum(o => o.NewText.Length - (o.End - o.Start));

            int finalOffset = plan.OriginalStart + shift + plan.InsertedLength;
            var position = new TextModel(_editor.GetText(documentId)).GetPosition(finalOffset);
            resultingCarets.Add(Caret.At(position));
        }

        _editor.SetCaretSet(documentId, new CaretSet(
            resultingCarets,
            Math.Min(caretSet.PrimaryIndex, resultingCarets.Count - 1)));
    }

    private static int PreviousTextElementStart(TextModel model, int offset)
    {
        char last = model.GetText(offset - 1, 1)[0];
        return offset >= 2 && char.IsLowSurrogate(last) &&
               char.IsHighSurrogate(model.GetText(offset - 2, 1)[0])
            ? offset - 2
            : offset - 1;
    }

    private static int NextTextElementEnd(TextModel model, int offset)
    {
        char first = model.GetText(offset, 1)[0];
        return offset + 1 < model.Length && char.IsHighSurrogate(first) &&
               char.IsLowSurrogate(model.GetText(offset + 1, 1)[0])
            ? offset + 2
            : offset + 1;
    }

    private static string GetLeadingWhitespace(string line)
    {
        int length = 0;
        while (length < line.Length && line[length] is ' ' or '\t') length++;
        return line[..length];
    }

    private static int CountLeadingSpaces(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
    }

    private sealed record PlannedOperation(int Start, int End, string NewText);
    private sealed record CaretPlan(int OriginalStart, int InsertedLength, PlannedOperation? Operation);
}
