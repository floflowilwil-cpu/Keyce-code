using App.Core.Errors;
using App.Core.Models;

namespace App.Editor.Documents;

/// <summary>
/// Un document ouvert : tampon de texte, fin de ligne d'origine, historique annuler/rétablir et état « modifié ».
/// Non thread-safe en lui-même : <see cref="SyncRoot"/> est le verrou à utiliser pour les accès concurrents.
/// </summary>
public sealed class Document
{
    private sealed record Change(int Offset, string Removed, string Inserted);

    private sealed class UndoGroup(bool isTyping, DateTimeOffset time)
    {
        public List<Change> Changes { get; } = new();
        public bool IsTyping { get; } = isTyping;
        public DateTimeOffset LastEditTime { get; set; } = time;
    }

    /// <summary>Pause maximale entre deux frappes pour qu'elles restent dans le même pas d'annulation.</summary>
    public static readonly TimeSpan TypingGroupWindow = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly List<UndoGroup> _undo = new();
    private readonly List<UndoGroup> _redo = new();
    private int _savedDepth; // taille de la pile d'annulation au dernier enregistrement ; -1 = état enregistré inatteignable
    private bool _breakCoalescing;

    public Document(DocumentId id, string? path, string text, TimeProvider? time = null)
    {
        Id = id;
        Path = path;
        LanguageId = LanguageIds.FromPath(path);
        LineEnding = LineEndingDetector.Detect(text);
        Text = new TextModel(text);
        _time = time ?? TimeProvider.System;
    }

    public object SyncRoot { get; } = new();
    public DocumentId Id { get; }
    public string? Path { get; }
    public string LanguageId { get; }
    public LineEnding LineEnding { get; set; }
    public TextModel Text { get; }

    /// <summary>Numéro de version, incrémenté à chaque modification (utile pour les notifications LSP).</summary>
    public int Version { get; private set; }

    public bool IsDirty => _savedDepth != _undo.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public DocumentInfo ToInfo() => new(Id, Path, LanguageId, IsDirty);

    /// <summary>Texte complet avec la fin de ligne d'origine, prêt à être écrit sur le disque.</summary>
    public string GetTextForSave()
    {
        var text = Text.GetText();
        return LineEnding == LineEnding.CrLf ? text.Replace("\n", "\r\n") : text;
    }

    public void MarkSaved()
    {
        _savedDepth = _undo.Count;
        _breakCoalescing = true;
    }

    /// <summary>
    /// Applique une modification. Retourne la modification effectivement appliquée (plage ramenée aux limites
    /// du document, fins de ligne normalisées), ou null si elle ne change rien.
    /// </summary>
    public TextEdit? Apply(TextEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        int start = Text.GetOffset(edit.Range.Start);
        int end = Text.GetOffset(edit.Range.End);
        if (end < start)
            throw new AppException(ErrorCode.InvalidArgument, "La fin de la plage précède son début.");

        string removed = Text.GetText(start, end - start);
        string inserted = TextModel.NormalizeNewLines(edit.NewText ?? string.Empty);
        if (removed.Length == 0 && inserted.Length == 0) return null;

        var applied = new TextEdit(new TextRange(Text.GetPosition(start), Text.GetPosition(end)), inserted);
        Text.Replace(start, end - start, inserted);
        Version++;
        Record(new Change(start, removed, inserted));
        return applied;
    }

    /// <summary>Annule le dernier pas. Retourne les modifications appliquées (dans l'ordre), ou null s'il n'y a rien à annuler.</summary>
    public IReadOnlyList<TextEdit>? Undo()
    {
        if (_undo.Count == 0) return null;
        var group = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        var edits = new List<TextEdit>(group.Changes.Count);
        for (int i = group.Changes.Count - 1; i >= 0; i--)
        {
            var c = group.Changes[i];
            int end = c.Offset + c.Inserted.Length;
            edits.Add(new TextEdit(new TextRange(Text.GetPosition(c.Offset), Text.GetPosition(end)), c.Removed));
            Text.Replace(c.Offset, c.Inserted.Length, c.Removed);
            Version++;
        }
        _redo.Add(group);
        _breakCoalescing = true;
        return edits;
    }

    /// <summary>Rétablit le dernier pas annulé. Retourne les modifications appliquées, ou null s'il n'y a rien à rétablir.</summary>
    public IReadOnlyList<TextEdit>? Redo()
    {
        if (_redo.Count == 0) return null;
        var group = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        var edits = new List<TextEdit>(group.Changes.Count);
        foreach (var c in group.Changes)
        {
            int end = c.Offset + c.Removed.Length;
            edits.Add(new TextEdit(new TextRange(Text.GetPosition(c.Offset), Text.GetPosition(end)), c.Inserted));
            Text.Replace(c.Offset, c.Removed.Length, c.Inserted);
            Version++;
        }
        _undo.Add(group);
        _breakCoalescing = true;
        return edits;
    }

    private void Record(Change change)
    {
        if (_redo.Count > 0)
        {
            if (_savedDepth > _undo.Count) _savedDepth = -1; // l'état enregistré se trouvait dans la branche rétablissable perdue
            _redo.Clear();
        }

        var now = _time.GetUtcNow();
        bool isTyping = change.Removed.Length == 0 && change.Inserted.Length > 0 && change.Inserted.IndexOf('\n') < 0;

        if (isTyping && !_breakCoalescing && _undo.Count > 0)
        {
            var last = _undo[^1];
            var lastChange = last.Changes[^1];
            if (last.IsTyping
                && lastChange.Removed.Length == 0
                && lastChange.Offset + lastChange.Inserted.Length == change.Offset
                && now - last.LastEditTime <= TypingGroupWindow)
            {
                last.Changes.Add(change);
                last.LastEditTime = now;
                return;
            }
        }

        var group = new UndoGroup(isTyping, now);
        group.Changes.Add(change);
        _undo.Add(group);
        _breakCoalescing = false;
    }
}
