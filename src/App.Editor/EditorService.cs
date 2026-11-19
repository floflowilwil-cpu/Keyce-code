using System.Collections.Concurrent;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;
using App.Editor.Documents;

namespace App.Editor;

/// <summary>
/// Implémentation de <see cref="IEditorService"/>.
/// Convention de <see cref="DocumentChanged"/> : les modifications d'un événement sont séquentielles —
/// chaque plage est exprimée dans l'état du document juste avant d'appliquer cette modification
/// (c'est ce qu'attend un didChange incrémental du LSP). L'événement est levé après la modification,
/// sur le thread de l'appelant.
/// </summary>
public sealed class EditorService : IEditorService
{
    private readonly IFileService _files;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<DocumentId, Document> _documents = new();
    private readonly ConcurrentDictionary<string, DocumentId> _byPath =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public EditorService(IFileService files, TimeProvider? time = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<DocumentChangedEventArgs> DocumentChanged = delegate { };

    /// <summary>Levé quand l'état d'un document change (modifié / enregistré) — pour le point « • » des onglets.</summary>
    public event EventHandler<DocumentInfo> DocumentStateChanged = delegate { };

    public async Task<DocumentInfo> OpenDocumentAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AppException(ErrorCode.InvalidArgument, "Le chemin du document est vide.");

        string full = Path.GetFullPath(path);
        if (TryGetByPath(full, out var existing)) return Snapshot(existing);

        string text = await _files.ReadAsync(full, ct).ConfigureAwait(false);
        var doc = new Document(DocumentId.New(), full, text, _time);

        _documents[doc.Id] = doc;
        if (!_byPath.TryAdd(full, doc.Id))
        {
            // Ouverture concurrente du même fichier : on garde le premier document.
            _documents.TryRemove(doc.Id, out _);
            if (TryGetByPath(full, out var winner)) return Snapshot(winner);
        }
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
            changedState = doc.IsDirty != wasDirty ? doc.ToInfo() : null;
        }
        if (applied is null) return;
        Raise(id, [applied], changedState);
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
        lock (doc.SyncRoot)
        {
            text = doc.GetTextForSave();
            version = doc.Version;
        }

        await _files.WriteAsync(doc.Path, text, ct).ConfigureAwait(false);

        DocumentInfo? changedState = null;
        lock (doc.SyncRoot)
        {
            // Si l'utilisateur a tapé pendant l'écriture, le document reste « modifié ».
            if (doc.Version == version)
            {
                bool wasDirty = doc.IsDirty;
                doc.MarkSaved();
                if (wasDirty) changedState = doc.ToInfo();
            }
        }
        if (changedState is not null) DocumentStateChanged(this, changedState);
    }

    /// <summary>Ferme un document (ne sauvegarde rien). Retourne false s'il n'était pas ouvert.</summary>
    public bool CloseDocument(DocumentId id)
    {
        if (!_documents.TryRemove(id, out var doc)) return false;
        if (doc.Path is not null) _byPath.TryRemove(doc.Path, out _);
        return true;
    }

    /// <summary>Accès direct au document (lecture des lignes pour l'affichage, etc.). À utiliser sur le thread UI.</summary>
    public Document GetDocument(DocumentId id) => Get(id);

    public DocumentInfo GetInfo(DocumentId id)
    {
        var doc = Get(id);
        lock (doc.SyncRoot) return doc.ToInfo();
    }

    public IReadOnlyList<DocumentInfo> GetOpenDocuments() =>
        _documents.Values.Select(Snapshot).ToList();

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
