using System.Collections.Concurrent;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;
using App.Editor.Documents;

namespace App.Editor;

/// <summary>
/// Service des documents. Les lots d'éditions sont fournis dans l'état initial et
/// appliqués de droite à gauche ; DocumentChanged contient l'ordre réel d'application.
/// </summary>
public sealed class EditorService : IEditorService, ITextModelSource, IDisposable
{
    private readonly IFileService _files;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<DocumentId, Document> _documents = new();
    private readonly ConcurrentDictionary<string, DocumentId> _byPath =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<DocumentId, CaretSet> _caretSets = new();
    private readonly ConcurrentDictionary<DocumentId, IDisposable> _watchers = new();

    public EditorService(IFileService files, TimeProvider? time = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<DocumentChangedEventArgs> DocumentChanged = delegate { };
    public event EventHandler<DocumentInfo> DocumentStateChanged = delegate { };

    /// <summary>
    /// Un fichier ouvert a changé sur le disque : rechargé (document propre), en conflit (document modifié)
    /// ou supprimé. Peut être levé depuis un thread d'arrière-plan.
    /// </summary>
    public event EventHandler<ExternalChangeEventArgs> ExternalChange = delegate { };

    public async Task<DocumentInfo> OpenDocumentAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AppException(ErrorCode.InvalidArgument, "Le chemin du document est vide.");

        string full = Path.GetFullPath(path);
        if (TryGetByPath(full, out var existing)) return Snapshot(existing);

        string text = await _files.ReadAsync(full, ct).ConfigureAwait(false);
        var doc = new Document(DocumentId.New(), full, text, _time);

        _documents[doc.Id] = doc;
        _caretSets.TryAdd(doc.Id, CaretSet.Single(new TextPosition(0, 0)));

        if (!_byPath.TryAdd(full, doc.Id))
        {
            _documents.TryRemove(doc.Id, out _);
            _caretSets.TryRemove(doc.Id, out _);
            if (TryGetByPath(full, out var winner)) return Snapshot(winner);
        }

        StartWatching(doc);
        return Snapshot(doc);
    }

    public string GetText(DocumentId id)
    {
        var doc = Get(id);
        lock (doc.SyncRoot) return doc.Text.GetText();
    }

    public void ApplyEdit(DocumentId id, TextEdit edit)
    {
        var doc = Get(id);
        TextEdit? applied;
        DocumentInfo? changedState;
        lock (doc.SyncRoot)
        {
            bool wasDirty = doc.IsDirty;
            applied = doc.Apply(edit);
            if (applied is not null) NormalizeCarets(id, doc);
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (applied is not null) Raise(id, [applied], changedState);
    }

    public void ApplyEdits(DocumentId id, IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var doc = Get(id);
        IReadOnlyList<TextEdit> applied;
        DocumentInfo? changedState;
        lock (doc.SyncRoot)
        {
            bool wasDirty = doc.IsDirty;
            applied = doc.ApplyBatch(edits);
            if (applied.Count > 0) NormalizeCarets(id, doc);
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (applied.Count > 0) Raise(id, applied, changedState);
    }

    /// <inheritdoc />
    public TextModel GetTextModel(DocumentId id) => Get(id).Text;

    public CaretSet GetCaretSet(DocumentId id)
    {
        _ = Get(id);
        return _caretSets.TryGetValue(id, out var carets)
            ? carets
            : CaretSet.Single(new TextPosition(0, 0));
    }

    public void SetCaretSet(DocumentId id, CaretSet carets)
    {
        ArgumentNullException.ThrowIfNull(carets);
        var doc = Get(id);

        lock (doc.SyncRoot)
        {
            foreach (var caret in carets.Carets)
            {
                ValidateDocumentPosition(doc.Text, caret.Position);
                ValidateDocumentPosition(doc.Text, caret.Anchor);
            }
            _caretSets[id] = carets;
        }
    }

    public void Undo(DocumentId id)
    {
        var doc = Get(id);
        IReadOnlyList<TextEdit>? edits;
        DocumentInfo? changedState;
        lock (doc.SyncRoot)
        {
            bool wasDirty = doc.IsDirty;
            edits = doc.Undo();
            if (edits is not null) PlaceCaretsAfterHistoryStep(id, doc);
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (edits is not null) Raise(id, edits, changedState);
    }

    public void Redo(DocumentId id)
    {
        var doc = Get(id);
        IReadOnlyList<TextEdit>? edits;
        DocumentInfo? changedState;
        lock (doc.SyncRoot)
        {
            bool wasDirty = doc.IsDirty;
            edits = doc.Redo();
            if (edits is not null) PlaceCaretsAfterHistoryStep(id, doc);
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (edits is not null) Raise(id, edits, changedState);
    }

    public async Task SaveAsync(DocumentId id, CancellationToken ct = default)
    {
        var doc = Get(id);
        if (doc.Path is null)
            throw new AppException(ErrorCode.InvalidArgument, "Ce document n'a pas de chemin : « Enregistrer sous » est nécessaire.");

        string text;
        int version;
        string previousDiskText;
        lock (doc.SyncRoot)
        {
            text = doc.GetTextForSave();
            version = doc.Version;
            previousDiskText = doc.DiskText;
            // Annoncé avant l'écriture : l'écho du watcher ne doit pas passer pour un changement externe.
            doc.DiskText = TextModel.NormalizeNewLines(text);
            doc.ConflictDiskText = null;
        }

        try
        {
            await _files.WriteAsync(doc.Path, text, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (doc.SyncRoot) doc.DiskText = previousDiskText;
            throw;
        }

        DocumentInfo? changedState = null;
        lock (doc.SyncRoot)
        {
            if (doc.Version == version)
            {
                bool wasDirty = doc.IsDirty;
                doc.MarkSaved();
                if (wasDirty) changedState = doc.ToInfo();
            }
        }
        if (changedState is not null) DocumentStateChanged(this, changedState);
    }

    public bool CloseDocument(DocumentId id)
    {
        if (!_documents.TryRemove(id, out var doc)) return false;
        _caretSets.TryRemove(id, out _);
        if (_watchers.TryRemove(id, out var watcher)) watcher.Dispose();
        if (doc.Path is not null) _byPath.TryRemove(doc.Path, out _);
        return true;
    }

    public void Dispose()
    {
        foreach (var id in _watchers.Keys.ToList())
            if (_watchers.TryRemove(id, out var watcher)) watcher.Dispose();
    }

    /// <summary>
    /// Compare le fichier au dernier contenu connu et réagit : recharge si le document est propre,
    /// signale un conflit s'il a des modifications non enregistrées, signale la suppression si le fichier a disparu.
    /// Appelée par le watcher ; l'interface peut aussi l'appeler (ex. au retour de focus de la fenêtre).
    /// </summary>
    public async Task CheckExternalChangeAsync(DocumentId id, CancellationToken ct = default)
    {
        var doc = Get(id);
        if (doc.Path is null) return;

        await doc.ExternalCheckLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string raw;
            try
            {
                raw = await _files.ReadAsync(doc.Path, ct).ConfigureAwait(false);
            }
            catch (AppException ex) when (ex.Code == ErrorCode.NotFound)
            {
                ExternalChange(this, new ExternalChangeEventArgs(id, doc.Path, ExternalChangeKind.Deleted));
                return;
            }
            catch (AppException)
            {
                return; // fichier verrouillé ou en cours d'écriture : le prochain événement réessaiera
            }

            string disk = TextModel.NormalizeNewLines(raw);

            IReadOnlyList<TextEdit>? edits = null;
            DocumentInfo? changedState = null;
            ExternalChangeKind? kind = null;

            lock (doc.SyncRoot)
            {
                if (!_documents.ContainsKey(id)) return; // fermé pendant la lecture
                if (disk == doc.DiskText) return;        // écho de notre sauvegarde, ou rien de nouveau

                if (!doc.IsDirty)
                {
                    var applied = doc.ReplaceFromDisk(raw);
                    if (applied is not null)
                    {
                        edits = [applied];
                        NormalizeCarets(id, doc);
                    }
                    kind = ExternalChangeKind.Reloaded;
                }
                else if (doc.ConflictDiskText != disk)
                {
                    doc.ConflictDiskText = disk;
                    kind = ExternalChangeKind.Conflict;
                }
            }

            if (edits is not null) Raise(id, edits, changedState);
            if (kind is not null) ExternalChange(this, new ExternalChangeEventArgs(id, doc.Path, kind.Value));
        }
        finally
        {
            doc.ExternalCheckLock.Release();
        }
    }

    /// <summary>Résout un conflit en abandonnant les modifications locales : recharge le fichier du disque.</summary>
    public async Task ReloadFromDiskAsync(DocumentId id, CancellationToken ct = default)
    {
        var doc = Get(id);
        if (doc.Path is null)
            throw new AppException(ErrorCode.InvalidArgument, "Ce document n'a pas de fichier à recharger.");

        await doc.ExternalCheckLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string raw = await _files.ReadAsync(doc.Path, ct).ConfigureAwait(false);

            TextEdit? applied;
            DocumentInfo? changedState;
            lock (doc.SyncRoot)
            {
                bool wasDirty = doc.IsDirty;
                applied = doc.ReplaceFromDisk(raw);
                if (applied is not null) NormalizeCarets(id, doc);
                changedState = wasDirty ? doc.ToInfo() : null;
            }
            if (applied is not null) Raise(id, [applied], changedState);
            else if (changedState is not null) DocumentStateChanged(this, changedState);
        }
        finally
        {
            doc.ExternalCheckLock.Release();
        }
    }

    /// <summary>
    /// Résout un conflit en gardant la version locale : la version disque actuelle est considérée comme connue,
    /// la prochaine sauvegarde l'écrasera. Le document reste « modifié ».
    /// </summary>
    public async Task KeepLocalVersionAsync(DocumentId id, CancellationToken ct = default)
    {
        var doc = Get(id);
        if (doc.Path is null) return;

        await doc.ExternalCheckLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string raw = await _files.ReadAsync(doc.Path, ct).ConfigureAwait(false);
            lock (doc.SyncRoot)
            {
                doc.DiskText = TextModel.NormalizeNewLines(raw);
                doc.ConflictDiskText = null;
            }
        }
        finally
        {
            doc.ExternalCheckLock.Release();
        }
    }

    private void StartWatching(Document doc)
    {
        if (doc.Path is null) return;
        var id = doc.Id;
        string path = doc.Path;

        try
        {
            var watcher = _files.WatchChanges(path, change => OnFileChanged(id, path, change));
            if (!_watchers.TryAdd(id, watcher)) watcher.Dispose();
        }
        catch (AppException)
        {
            // Surveillance indisponible : le document reste utilisable, sans détection automatique.
        }
    }

    private void OnFileChanged(DocumentId id, string path, FileChange change)
    {
        bool movedAway = change.Kind == FileChangeKind.Renamed &&
                         change.OldPath is not null &&
                         PathsEqual(change.OldPath, path);

        _ = Task.Run(async () =>
        {
            try
            {
                if (change.Kind == FileChangeKind.Deleted || movedAway)
                    ExternalChange(this, new ExternalChangeEventArgs(id, path, ExternalChangeKind.Deleted));
                else
                    await CheckExternalChangeAsync(id).ConfigureAwait(false);
            }
            catch
            {
                // Document fermé entre-temps, ou erreur d'un abonné : on ne fait pas tomber le processus.
            }
        });
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public Document GetDocument(DocumentId id) => Get(id);

    public DocumentInfo GetInfo(DocumentId id)
    {
        var doc = Get(id);
        lock (doc.SyncRoot) return doc.ToInfo();
    }

    public IReadOnlyList<DocumentInfo> GetOpenDocuments() =>
        _documents.Values.Select(Snapshot).ToList();

    /// <summary>Après Undo/Redo : curseur(s) à la fin du texte restauré ou réinséré.</summary>
    private void PlaceCaretsAfterHistoryStep(DocumentId id, Document doc)
    {
        var positions = doc.CaretOffsetsAfterHistoryStep
            .Select(offset => doc.Text.GetPosition(Math.Clamp(offset, 0, doc.Text.Length)))
            .Distinct()
            .ToList();
        if (positions.Count > 0) _caretSets[id] = new CaretSet(positions.Select(Caret.At));
    }

    /// <summary>Après une modification : ramène dans le document les curseurs qui en sortiraient.</summary>
    private void NormalizeCarets(DocumentId id, Document doc)
    {
        if (!_caretSets.TryGetValue(id, out var carets)) return;
        var text = doc.Text;
        if (carets.Carets.All(c => IsInside(text, c.Position) && IsInside(text, c.Anchor))) return;

        TextPosition Clamp(TextPosition p) => text.GetPosition(text.GetOffset(p));
        var seen = new HashSet<TextPosition>();
        var unique = carets.Carets
            .Select(c => new Caret(Clamp(c.Position), Clamp(c.Anchor)))
            .Where(c => seen.Add(c.Position))
            .ToList();
        int primary = Math.Clamp(carets.PrimaryIndex, 0, unique.Count - 1);

        try { _caretSets[id] = new CaretSet(unique, primary); }
        catch (ArgumentException) // sélections qui se chevauchent après ajustement : on garde seulement les curseurs
        {
            _caretSets[id] = new CaretSet(unique.Select(c => Caret.At(c.Position)), primary);
        }
    }

    private static bool IsInside(TextModel text, TextPosition position) =>
        position.Line >= 0 && position.Line < text.LineCount &&
        position.Column >= 0 && position.Column <= text.GetLineLength(position.Line);

    private static void ValidateDocumentPosition(TextModel text, TextPosition position)
    {
        if (position.Line < 0 || position.Line >= text.LineCount ||
            position.Column < 0 || position.Column > text.GetLineLength(position.Line))
            throw new AppException(ErrorCode.InvalidArgument, $"Position de curseur hors document : {position}.");
    }

    private void Raise(DocumentId id, IReadOnlyList<TextEdit> edits, DocumentInfo? changedState)
    {
        DocumentChanged(this, new DocumentChangedEventArgs(id, edits));
        if (changedState is not null) DocumentStateChanged(this, changedState);
    }

    private DocumentInfo Snapshot(Document doc)
    {
        lock (doc.SyncRoot) return doc.ToInfo();
    }

    private bool TryGetByPath(string fullPath, out Document doc)
    {
        if (_byPath.TryGetValue(fullPath, out var id) && _documents.TryGetValue(id, out var found))
        {
            doc = found;
            return true;
        }
        doc = null!;
        return false;
    }

    private Document Get(DocumentId id) =>
        _documents.TryGetValue(id, out var doc)
            ? doc
            : throw new AppException(ErrorCode.NotFound, $"Document introuvable : {id.Value}.");
}
