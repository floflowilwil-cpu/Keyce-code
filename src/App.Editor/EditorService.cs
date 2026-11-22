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
    public event EventHandler<DocumentInfo> DocumentOpened = delegate { };
    public event EventHandler<DocumentInfo> DocumentClosed = delegate { };

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

        var content = await ReadContentAsync(full, ct).ConfigureAwait(false);
        var doc = new Document(DocumentId.New(), full, content.Text, _time) { Encoding = content.Encoding };

        _documents[doc.Id] = doc;
        _caretSets.TryAdd(doc.Id, CaretSet.Single(new TextPosition(0, 0)));

        if (!_byPath.TryAdd(full, doc.Id))
        {
            _documents.TryRemove(doc.Id, out _);
            _caretSets.TryRemove(doc.Id, out _);
            if (TryGetByPath(full, out var winner)) return Snapshot(winner);
        }

        StartWatching(doc);
        var opened = Snapshot(doc);
        DocumentOpened(this, opened);
        return opened;
    }

    public string GetText(DocumentId id)
    {
        var doc = Get(id);
        lock (doc.SyncRoot) return doc.Text.GetText();
    }

    public int GetLineCount(DocumentId id)
    {
        var doc = Get(id);
        lock (doc.SyncRoot) return doc.Text.LineCount;
    }

    public string GetLineText(DocumentId id, int line)
    {
        var doc = Get(id);
        lock (doc.SyncRoot)
        {
            if (line < 0 || line >= doc.Text.LineCount)
                throw new AppException(ErrorCode.InvalidArgument, $"Ligne hors document : {line}.");
            return doc.Text.GetLineText(line);
        }
    }

    public void ApplyEdit(DocumentId id, TextEdit edit)
    {
        var doc = Get(id);
        TextEdit? applied;
        DocumentInfo? changedState;
        int version;
        lock (doc.SyncRoot)
        {
            bool wasDirty = doc.IsDirty;
            applied = doc.Apply(edit);
            if (applied is not null) NormalizeCarets(id, doc);
            version = doc.Version;
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (applied is not null) Raise(id, [applied], changedState, version);
    }

    public void ApplyEdits(DocumentId id, IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var doc = Get(id);
        IReadOnlyList<TextEdit> applied;
        DocumentInfo? changedState;
        int version;
        lock (doc.SyncRoot)
        {
            bool wasDirty = doc.IsDirty;
            applied = doc.ApplyBatch(edits);
            if (applied.Count > 0) NormalizeCarets(id, doc);
            version = doc.Version;
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (applied.Count > 0) Raise(id, applied, changedState, version);
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
        int version;
        lock (doc.SyncRoot)
        {
            bool wasDirty = doc.IsDirty;
            edits = doc.Undo();
            if (edits is not null) PlaceCaretsAfterHistoryStep(id, doc);
            version = doc.Version;
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (edits is not null) Raise(id, edits, changedState, version);
    }

    public void Redo(DocumentId id)
    {
        var doc = Get(id);
        IReadOnlyList<TextEdit>? edits;
        DocumentInfo? changedState;
        int version;
        lock (doc.SyncRoot)
        {
            bool wasDirty = doc.IsDirty;
            edits = doc.Redo();
            if (edits is not null) PlaceCaretsAfterHistoryStep(id, doc);
            version = doc.Version;
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (edits is not null) Raise(id, edits, changedState, version);
    }

    public async Task SaveAsync(DocumentId id, CancellationToken ct = default)
    {
        var doc = Get(id);
        if (doc.Path is null)
            throw new AppException(ErrorCode.InvalidArgument, "Ce document n'a pas de chemin : « Enregistrer sous » est nécessaire.");

        string text;
        int version;
        string previousDiskText;
        FileEncoding encoding;
        lock (doc.SyncRoot)
        {
            text = doc.GetTextForSave();
            version = doc.Version;
            encoding = doc.Encoding;
            previousDiskText = doc.DiskText;
            // Annoncé avant l'écriture : l'écho du watcher ne doit pas passer pour un changement externe.
            doc.DiskText = TextModel.NormalizeNewLines(text);
            doc.ConflictDiskText = null;
        }

        try
        {
            await WriteContentAsync(doc.Path, text, encoding, ct).ConfigureAwait(false);
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

    public FileEncoding GetEncoding(DocumentId id)
    {
        var doc = Get(id);
        lock (doc.SyncRoot) return doc.Encoding;
    }

    public void SetEncoding(DocumentId id, FileEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        if (!FileEncoding.Supported.Contains(encoding))
            throw new AppException(ErrorCode.InvalidArgument, $"Encodage non pris en charge : {encoding.Name}.");

        var doc = Get(id);
        DocumentInfo? info = null;
        lock (doc.SyncRoot)
        {
            if (doc.Encoding != encoding)
            {
                doc.Encoding = encoding;
                info = doc.ToInfo();
            }
        }
        if (info is not null) DocumentStateChanged(this, info);
    }

    /// <summary>Relit le fichier d'un document ouvert : avec l'encodage imposé par l'utilisateur s'il y en a un, sinon détecté.</summary>
    private async Task<FileContent> ReadContentForAsync(Document doc, CancellationToken ct)
    {
        string path = doc.Path!;
        FileEncoding? forced;
        lock (doc.SyncRoot) forced = doc.EncodingForced ? doc.Encoding : null;

        if (forced is not null && _files is IEncodedFileService encoded)
        {
            try { return await encoded.ReadAsEncodingAsync(path, forced, ct).ConfigureAwait(false); }
            catch (NotSupportedException) { /* service sans cette fonction : détection automatique */ }
        }
        return await ReadContentAsync(path, ct).ConfigureAwait(false);
    }

    public LineEndingStyle GetLineEnding(DocumentId id)
    {
        var doc = Get(id);
        lock (doc.SyncRoot) return doc.LineEnding == LineEnding.CrLf ? LineEndingStyle.CrLf : LineEndingStyle.Lf;
    }

    public void SetLineEnding(DocumentId id, LineEndingStyle style)
    {
        if (!Enum.IsDefined(style))
            throw new AppException(ErrorCode.InvalidArgument, $"Fin de ligne inconnue : {style}.");

        var doc = Get(id);
        DocumentInfo? info = null;
        lock (doc.SyncRoot)
        {
            var wanted = style == LineEndingStyle.CrLf ? LineEnding.CrLf : LineEnding.Lf;
            if (doc.LineEnding != wanted)
            {
                doc.LineEnding = wanted;
                info = doc.ToInfo();
            }
        }
        if (info is not null) DocumentStateChanged(this, info);
    }

    public async Task ReopenWithEncodingAsync(DocumentId id, FileEncoding encoding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        if (!FileEncoding.Supported.Contains(encoding))
            throw new AppException(ErrorCode.InvalidArgument, $"Encodage non pris en charge : {encoding.Name}.");

        var doc = Get(id);
        if (doc.Path is null)
            throw new AppException(ErrorCode.InvalidArgument, "Ce document n'a pas de fichier à relire.");
        if (_files is not IEncodedFileService encoded)
            throw new AppException(ErrorCode.InvalidArgument, "Ce service de fichiers ne gère pas les encodages.");

        await doc.ExternalCheckLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            FileContent content;
            try
            {
                content = await encoded.ReadAsEncodingAsync(doc.Path, encoding, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                throw new AppException(ErrorCode.InvalidArgument, ex.Message, ex);
            }

            TextEdit? applied;
            DocumentInfo changedState;
            int version;
            lock (doc.SyncRoot)
            {
                if (doc.IsDirty)
                    throw new AppException(ErrorCode.Conflict,
                        "Le document a des modifications non enregistrées : enregistrez-les ou annulez-les avant de changer d'encodage.");

                doc.Encoding = content.Encoding;
                doc.EncodingForced = true;
                applied = doc.ReplaceFromDisk(content.Text);
                if (applied is not null) NormalizeCarets(id, doc);
                changedState = doc.ToInfo();
                version = doc.Version;
            }
            if (applied is not null) Raise(id, [applied], changedState, version);
            else DocumentStateChanged(this, changedState);
        }
        finally
        {
            doc.ExternalCheckLock.Release();
        }
    }

    public async Task<IReadOnlyList<DocumentInfo>> MovePathAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        string from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldPath));
        string to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newPath));

        var affected = _documents.Values
            .Where(d => d.Path is not null && IsSameOrInside(d.Path, from))
            .ToList();

        // Pendant le renommage, les événements de l'ancien chemin ne doivent pas faire croire à une suppression.
        foreach (var d in affected) d.IsMoving = true;
        try
        {
            try
            {
                await _files.MoveAsync(from, to, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                throw new AppException(ErrorCode.InvalidArgument, ex.Message, ex);
            }

            var infos = new List<DocumentInfo>();
            foreach (var doc in affected)
            {
                string previous;
                DocumentInfo info;
                lock (doc.SyncRoot)
                {
                    previous = doc.Path!;
                    doc.SetPath(Path.GetFullPath(to + previous[from.Length..]));
                    info = doc.ToInfo();
                }

                _byPath.TryRemove(previous, out _);
                _byPath.TryAdd(info.Path!, doc.Id);

                if (_watchers.TryRemove(doc.Id, out var oldWatcher)) oldWatcher.Dispose();
                StartWatching(doc);
                infos.Add(info);
            }

            foreach (var info in infos) DocumentStateChanged(this, info);
            return infos;
        }
        finally
        {
            foreach (var d in affected) d.IsMoving = false;
        }
    }

    private static bool IsSameOrInside(string path, string folderOrFile)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string full = Path.GetFullPath(path);
        return string.Equals(full, folderOrFile, comparison) ||
               full.StartsWith(folderOrFile + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Lit un fichier avec détection d'encodage si le service de fichiers le permet (sinon UTF-8).</summary>
    private async Task<FileContent> ReadContentAsync(string path, CancellationToken ct)
    {
        if (_files is IEncodedFileService encoded)
            return await encoded.ReadWithEncodingAsync(path, ct).ConfigureAwait(false);

        string text = await _files.ReadAsync(path, ct).ConfigureAwait(false);
        return new FileContent(text, FileEncoding.Utf8);
    }

    private Task WriteContentAsync(string path, string text, FileEncoding encoding, CancellationToken ct) =>
        _files is IEncodedFileService encoded
            ? encoded.WriteAsync(path, text, encoding, ct)
            : _files.WriteAsync(path, text, ct);

    public DocumentInfo CreateDocument()
    {
        var doc = new Document(DocumentId.New(), null, string.Empty, _time);
        _documents[doc.Id] = doc;
        _caretSets[doc.Id] = CaretSet.Single(new TextPosition(0, 0));
        var created = Snapshot(doc);
        DocumentOpened(this, created);
        return created;
    }

    public async Task SaveAsAsync(DocumentId id, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AppException(ErrorCode.InvalidArgument, "Le chemin du fichier est vide.");

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AppException(ErrorCode.InvalidArgument, $"Chemin invalide : {path}", ex);
        }

        var doc = Get(id);

        if (TryGetByPath(full, out var other) && other.Id != id)
            throw new AppException(ErrorCode.Conflict, $"Ce fichier est déjà ouvert dans un autre document : {full}");

        if (doc.Path is not null && PathsEqual(doc.Path, full))
        {
            await SaveAsync(id, ct).ConfigureAwait(false);
            return;
        }

        string text;
        int version;
        FileEncoding encoding;
        lock (doc.SyncRoot)
        {
            text = doc.GetTextForSave();
            version = doc.Version;
            encoding = doc.Encoding;
        }

        await WriteContentAsync(full, text, encoding, ct).ConfigureAwait(false);

        string? oldPath;
        DocumentInfo info;
        lock (doc.SyncRoot)
        {
            oldPath = doc.Path;
            doc.SetPath(full);
            doc.DiskText = TextModel.NormalizeNewLines(text);
            doc.ConflictDiskText = null;
            if (doc.Version == version) doc.MarkSaved(); // sinon : modifié pendant l'écriture, il reste « modifié »
            info = doc.ToInfo();
        }

        if (oldPath is not null) _byPath.TryRemove(oldPath, out _);
        _byPath.TryAdd(full, id);

        if (_watchers.TryRemove(id, out var oldWatcher)) oldWatcher.Dispose();
        StartWatching(doc);

        DocumentStateChanged(this, info);
    }

    public bool CloseDocument(DocumentId id)
    {
        if (!_documents.TryRemove(id, out var doc)) return false;
        _caretSets.TryRemove(id, out _);
        if (_watchers.TryRemove(id, out var watcher)) watcher.Dispose();
        if (doc.Path is not null) _byPath.TryRemove(doc.Path, out _);
        DocumentClosed(this, Snapshot(doc));
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
            FileContent content;
            try
            {
                content = await ReadContentForAsync(doc, ct).ConfigureAwait(false);
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

            string raw = content.Text;
            string disk = TextModel.NormalizeNewLines(raw);

            IReadOnlyList<TextEdit>? edits = null;
            DocumentInfo? changedState = null;
            ExternalChangeKind? kind = null;
            int version = 0;

            lock (doc.SyncRoot)
            {
                if (!_documents.ContainsKey(id)) return; // fermé pendant la lecture
                if (disk == doc.DiskText) return;        // écho de notre sauvegarde, ou rien de nouveau

                if (!doc.IsDirty)
                {
                    doc.Encoding = content.Encoding;
                    var applied = doc.ReplaceFromDisk(raw);
                    if (applied is not null)
                    {
                        edits = [applied];
                        NormalizeCarets(id, doc);
                        version = doc.Version;
                    }
                    kind = ExternalChangeKind.Reloaded;
                }
                else if (doc.ConflictDiskText != disk)
                {
                    doc.ConflictDiskText = disk;
                    kind = ExternalChangeKind.Conflict;
                }
            }

            if (edits is not null) Raise(id, edits, changedState, version);
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
            var content = await ReadContentForAsync(doc, ct).ConfigureAwait(false);
            string raw = content.Text;

            TextEdit? applied;
            DocumentInfo? changedState;
            int version;
            lock (doc.SyncRoot)
            {
                bool wasDirty = doc.IsDirty;
                doc.Encoding = content.Encoding;
                applied = doc.ReplaceFromDisk(raw);
                if (applied is not null) NormalizeCarets(id, doc);
                version = doc.Version;
                changedState = wasDirty ? doc.ToInfo() : null;
            }
            if (applied is not null) Raise(id, [applied], changedState, version);
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
            string raw = (await ReadContentForAsync(doc, ct).ConfigureAwait(false)).Text;
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
        // Renommage en cours, ou document déjà déplacé : l'événement concerne l'ancien chemin.
        if (_documents.TryGetValue(id, out var current) && (current.IsMoving || current.Path is null || !PathsEqual(current.Path, path)))
            return;

        bool movedAway = change.Kind == FileChangeKind.Renamed &&
                         change.OldPath is not null &&
                         PathsEqual(change.OldPath, path);

        _ = Task.Run(async () =>
        {
            try
            {
                if (_documents.TryGetValue(id, out var d) && (d.Path is null || !PathsEqual(d.Path, path))) return;
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

    private void Raise(DocumentId id, IReadOnlyList<TextEdit> edits, DocumentInfo? changedState, int version)
    {
        DocumentChanged(this, new DocumentChangedEventArgs(id, edits, version));
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
