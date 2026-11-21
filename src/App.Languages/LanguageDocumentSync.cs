using System.Collections.Concurrent;
using System.Threading.Channels;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Languages;

public sealed class DocumentDiagnosticsEventArgs(DocumentId id, IReadOnlyList<Diagnostic> diagnostics) : EventArgs
{
    public DocumentId Id { get; } = id;
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Relie l'éditeur au client LSP : ouvre, modifie et ferme les documents côté serveur,
/// et renvoie les diagnostics par DocumentId. Personne n'a besoin de l'appeler.
/// </summary>
public sealed class LanguageDocumentSync : IDisposable
{
    private sealed class SyncDoc(string key, string languageId)
    {
        public string Key { get; } = key;                 // chemin complet ou « untitled:Untitled-N »
        public string LanguageId { get; } = languageId;
        public volatile bool NeedsReopen;                 // le serveur a planté : renvoyer le texte complet
    }

    private readonly IEditorService _editor;
    private readonly LanguageClient _client;
    private readonly ConcurrentDictionary<DocumentId, SyncDoc> _docs = new();
    private readonly ConcurrentDictionary<string, DocumentId> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<DocumentId, string> _untitled = new();
    private readonly Channel<Func<Task>> _jobs = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true });
    private int _untitledCounter;

    /// <summary>Dossier de travail envoyé aux serveurs de langage.</summary>
    public string WorkspaceRoot { get; set; } = Directory.GetCurrentDirectory();

    public event EventHandler<DocumentDiagnosticsEventArgs> DiagnosticsPublished = delegate { };

    /// <summary>Une opération LSP a échoué (serveur introuvable, délai dépassé...). L'interface peut l'afficher.</summary>
    public event EventHandler<AppException>? ErrorOccurred;

    public LanguageDocumentSync(IEditorService editor, LanguageClient client)
    {
        _editor = editor;
        _client = client;
        _ = Task.Run(RunAsync);

        _client.DiagnosticsPublished += OnDiagnostics;
        _client.ServerDisconnected += OnServerDisconnected;

        // On s'abonne AVANT de lire GetOpenDocuments() pour ne rater aucun document.
        _editor.DocumentOpened += OnOpened;
        _editor.DocumentClosed += OnClosed;
        _editor.DocumentChanged += OnChanged;
        _editor.DocumentStateChanged += OnStateChanged;

        foreach (var info in _editor.GetOpenDocuments()) Open(info);
    }

    public void Dispose()
    {
        _editor.DocumentOpened -= OnOpened;
        _editor.DocumentClosed -= OnClosed;
        _editor.DocumentChanged -= OnChanged;
        _editor.DocumentStateChanged -= OnStateChanged;
        _client.DiagnosticsPublished -= OnDiagnostics;
        _client.ServerDisconnected -= OnServerDisconnected;
        _jobs.Writer.TryComplete();
    }

    // ---------- Événements de l'éditeur (peuvent arriver de n'importe quel thread) ----------

    private void OnOpened(object? sender, DocumentInfo info) => Open(info);

    private void OnClosed(object? sender, DocumentInfo info) => Remove(info.Id);

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (!_docs.TryGetValue(e.Id, out var doc)) return;

        if (doc.NeedsReopen)
        {
            // Le serveur ne connaît plus ce document : on lui renvoie le texte complet d'un coup.
            string text;
            try { text = _editor.GetText(e.Id); }
            catch (AppException) { return; }

            doc.NeedsReopen = false;
            Enqueue(() => OpenOnServerAsync(doc, text));
            return;
        }

        var id = e.Id;
        var edits = e.Edits;
        Enqueue(async () =>
        {
            try { await _client.ChangeDocumentAsync(doc.Key, edits, () => _editor.GetText(id)); }
            catch { doc.NeedsReopen = true; throw; }
        });
    }

    private void OnStateChanged(object? sender, DocumentInfo info)
    {
        if (_docs.TryGetValue(info.Id, out var doc))
        {
            // Seul l'état « modifié » a changé : rien à faire.
            if (doc.LanguageId == info.LanguageId && doc.Key == KeyFor(info)) return;

            Remove(info.Id);   // chemin ou langage différent (« Enregistrer sous ») : on ferme l'ancien
        }
        Open(info);
    }

    private void OnServerDisconnected(object? sender, string languageId)
    {
        foreach (var doc in _docs.Values)
            if (string.Equals(doc.LanguageId, languageId, StringComparison.OrdinalIgnoreCase))
                doc.NeedsReopen = true;
    }

    private void OnDiagnostics(object? sender, DiagnosticsPublishedEventArgs e)
    {
        if (_byKey.TryGetValue(e.Path, out var id))
            DiagnosticsPublished(this, new DocumentDiagnosticsEventArgs(id, e.Diagnostics));
    }

    // ---------- Gestion des documents ----------

    private void Open(DocumentInfo info)
    {
        if (!LanguageClient.SupportsLanguage(info.LanguageId)) return;

        var key = KeyFor(info);
        if (_docs.TryGetValue(info.Id, out var existing)
            && existing.Key == key && existing.LanguageId == info.LanguageId) return;

        string text;
        try { text = _editor.GetText(info.Id); }
        catch (AppException) { return; }   // document fermé entre-temps

        var doc = new SyncDoc(key, info.LanguageId);
        _docs[info.Id] = doc;
        _byKey[key] = info.Id;
        Enqueue(() => OpenOnServerAsync(doc, text));
    }

    private void Remove(DocumentId id)
    {
        if (!_docs.TryRemove(id, out var doc)) return;

        _byKey.TryRemove(new KeyValuePair<string, DocumentId>(doc.Key, id));
        _untitled.TryRemove(id, out _);

        Enqueue(async () =>
        {
            try { await _client.CloseDocumentAsync(doc.Key); }
            catch (AppException e) when (e.Code == ErrorCode.Unavailable) { /* serveur déjà arrêté */ }
        });
    }

    private async Task OpenOnServerAsync(SyncDoc doc, string text)
    {
        try
        {
            await _client.StartServerAsync(doc.LanguageId, WorkspaceRoot);   // idempotent
            await _client.OpenDocumentAsync(doc.Key, doc.LanguageId, text);
        }
        catch
        {
            doc.NeedsReopen = true;   // nouvelle tentative à la prochaine modification
            throw;
        }
    }

    private string KeyFor(DocumentInfo info)
        => !string.IsNullOrEmpty(info.Path)
            ? Path.GetFullPath(info.Path)
            : _untitled.GetOrAdd(info.Id, _ => $"untitled:Untitled-{Interlocked.Increment(ref _untitledCounter)}");

    // ---------- File d'attente : un seul message à la fois, dans l'ordre ----------

    private void Enqueue(Func<Task> job) => _jobs.Writer.TryWrite(job);

    private async Task RunAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync())
        {
            try
            {
                await job();
            }
            catch (AppException e)
            {
                ErrorOccurred?.Invoke(this, e);
            }
            catch (Exception e)
            {
                ErrorOccurred?.Invoke(this, new AppException(ErrorCode.Unknown, e.Message, e));
            }
        }
    }
}