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

    public static readonly TimeSpan TypingGroupWindow = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly List<UndoGroup> _undo = new();
    private readonly List<UndoGroup> _redo = new();
    private int _savedDepth;
    private bool _breakCoalescing;

    public Document(DocumentId id, string? path, string text, TimeProvider? time = null)
    {
        Id = id;
        Path = path;
        LanguageId = LanguageIds.FromPath(path);
        LineEnding = LineEndingDetector.Detect(text);
        Text = new TextModel(text);
        DiskText = TextModel.NormalizeNewLines(text);
        _time = time ?? TimeProvider.System;
    }

    public object SyncRoot { get; } = new();
    public DocumentId Id { get; }
    public string? Path { get; }
    public string LanguageId { get; }
    public LineEnding LineEnding { get; set; }
    public TextModel Text { get; }
    public int Version { get; private set; }
    public bool IsDirty => _savedDepth != _undo.Count;

    /// <summary>
    /// Dernier contenu connu du fichier sur le disque (fins de ligne normalisées en \n) :
    /// ce qui a été lu à l'ouverture, au rechargement, ou écrit lors de la dernière sauvegarde.
    /// Sert à distinguer une vraie modification externe de l'écho de notre propre sauvegarde.
    /// </summary>
    public string DiskText { get; set; }

    /// <summary>Contenu disque ayant déjà provoqué un conflit signalé (évite de le signaler en boucle).</summary>
    public string? ConflictDiskText { get; set; }

    /// <summary>Verrou qui sérialise les vérifications de changements externes de ce document.</summary>
    public SemaphoreSlim ExternalCheckLock { get; } = new(1, 1);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Décalages où placer les curseurs après le dernier <see cref="Undo"/> ou <see cref="Redo"/> :
    /// la fin du texte restauré (Undo) ou réinséré (Redo), un curseur par modification pour un lot multi-curseurs.
    /// </summary>
    public IReadOnlyList<int> CaretOffsetsAfterHistoryStep { get; private set; } = Array.Empty<int>();

    public DocumentInfo ToInfo() => new(Id, Path, LanguageId, IsDirty);

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
    /// Remplace tout le contenu par celui du disque : l'historique est effacé et le document redevient propre.
    /// Retourne la modification globale appliquée, ou null si le texte est identique.
    /// </summary>
    public TextEdit? ReplaceFromDisk(string rawDiskText)
    {
        ArgumentNullException.ThrowIfNull(rawDiskText);

        string normalized = TextModel.NormalizeNewLines(rawDiskText);
        TextEdit? applied = null;

        if (Text.GetText() != normalized)
        {
            var range = new TextRange(new TextPosition(0, 0), Text.GetPosition(Text.Length));
            Text.Replace(0, Text.Length, normalized);
            Version++;
            applied = new TextEdit(range, normalized);
        }

        LineEnding = LineEndingDetector.Detect(rawDiskText);
        DiskText = normalized;
        ConflictDiskText = null;
        _undo.Clear();
        _redo.Clear();
        _savedDepth = 0;
        _breakCoalescing = true;
        CaretOffsetsAfterHistoryStep = Array.Empty<int>();
        return applied;
    }

    public TextEdit? Apply(TextEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var applied = ApplyCore(edit, out var change);
        if (applied is null) return null;

        Record(change!);
        return applied;
    }

    /// <summary>
    /// Applique des modifications dont les plages sont toutes exprimées dans le texte
    /// avant transaction. Les plages qui se chevauchent sont rejetées. L'application
    /// se fait de droite à gauche afin de préserver les offsets d'origine.
    /// </summary>
    public IReadOnlyList<TextEdit> ApplyBatch(IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) return Array.Empty<TextEdit>();

        var prepared = new List<(int Start, int End, string NewText)>(edits.Count);
        foreach (var edit in edits)
        {
            ArgumentNullException.ThrowIfNull(edit);
            int start = Text.GetOffset(edit.Range.Start);
            int end = Text.GetOffset(edit.Range.End);
            if (end < start)
                throw new AppException(ErrorCode.InvalidArgument, "La fin de la plage précède son début.");

            prepared.Add((start, end, TextModel.NormalizeNewLines(edit.NewText ?? string.Empty)));
        }

        prepared.Sort((a, b) =>
        {
            int byStartDescending = b.Start.CompareTo(a.Start);
            return byStartDescending != 0 ? byStartDescending : b.End.CompareTo(a.End);
        });

        for (int i = 1; i < prepared.Count; i++)
        {
            var previous = prepared[i - 1];
            var current = prepared[i];

            // Une insertion à l'intérieur d'une plage, deux insertions au même offset
            // ou deux plages qui se recouvrent rendraient le résultat ambigu.
            if (current.End > previous.Start ||
                (current.Start == current.End && previous.Start == previous.End &&
                 current.Start == previous.Start))
            {
                throw new AppException(ErrorCode.InvalidArgument,
                    "Les modifications d'un même lot ne doivent pas se chevaucher.");
            }
        }

        var group = new UndoGroup(isTyping: false, _time.GetUtcNow());
        var applied = new List<TextEdit>(prepared.Count);

        foreach (var item in prepared)
        {
            string removed = Text.GetText(item.Start, item.End - item.Start);
            if (removed.Length == 0 && item.NewText.Length == 0)
                continue;

            var range = new TextRange(Text.GetPosition(item.Start), Text.GetPosition(item.End));
            Text.Replace(item.Start, item.End - item.Start, item.NewText);
            Version++;

            group.Changes.Add(new Change(item.Start, removed, item.NewText));
            applied.Add(new TextEdit(range, item.NewText));
        }

        if (group.Changes.Count == 0)
            return Array.Empty<TextEdit>();

        ClearRedoBranch();
        _undo.Add(group);
        _breakCoalescing = true;
        return applied;
    }

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
        CaretOffsetsAfterHistoryStep = ComputeCaretOffsets(group, undo: true);
        _breakCoalescing = true;
        return edits;
    }

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
        CaretOffsetsAfterHistoryStep = ComputeCaretOffsets(group, undo: false);
        _breakCoalescing = true;
        return edits;
    }

    private TextEdit? ApplyCore(TextEdit edit, out Change? change)
    {
        int start = Text.GetOffset(edit.Range.Start);
        int end = Text.GetOffset(edit.Range.End);
        if (end < start)
            throw new AppException(ErrorCode.InvalidArgument, "La fin de la plage précède son début.");

        string removed = Text.GetText(start, end - start);
        string inserted = TextModel.NormalizeNewLines(edit.NewText ?? string.Empty);
        if (removed.Length == 0 && inserted.Length == 0)
        {
            change = null;
            return null;
        }

        var applied = new TextEdit(new TextRange(Text.GetPosition(start), Text.GetPosition(end)), inserted);
        Text.Replace(start, end - start, inserted);
        Version++;
        change = new Change(start, removed, inserted);
        return applied;
    }

    private void Record(Change change)
    {
        ClearRedoBranch();

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

    private static IReadOnlyList<int> ComputeCaretOffsets(UndoGroup group, bool undo)
    {
        // Frappes regroupées ou modification unique : un seul curseur.
        if (group.IsTyping || group.Changes.Count == 1)
        {
            return undo
                ? [group.Changes[0].Offset + group.Changes[0].Removed.Length]
                : [group.Changes[^1].Offset + group.Changes[^1].Inserted.Length];
        }

        // Lot multi-curseurs : les modifications sont enregistrées de droite à gauche, dans les décalages
        // d'origine. On place un curseur par modification, de gauche à droite.
        var offsets = new SortedSet<int>();
        int shift = 0;
        foreach (var c in group.Changes.OrderBy(c => c.Offset))
        {
            offsets.Add(undo
                ? c.Offset + c.Removed.Length
                : c.Offset + shift + c.Inserted.Length);
            shift += c.Inserted.Length - c.Removed.Length;
        }
        return offsets.ToList();
    }

    private void ClearRedoBranch()
    {
        if (_redo.Count == 0) return;
        if (_savedDepth > _undo.Count) _savedDepth = -1;
        _redo.Clear();
    }
}
