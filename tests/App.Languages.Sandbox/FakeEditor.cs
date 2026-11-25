using App.Core.Contracts;
using App.Core.Models;

/// <summary>Faux éditeur pour tester sans l'éditeur du Dev 2. Seules les fonctions utiles aux tests existent.</summary>
sealed class FakeEditor : IEditorService
{
    private sealed class Doc(string text, string? filePath, string languageId)
    {
        public string Text = text;
        public string? FilePath = filePath;
        public string LanguageId = languageId;
    }

    private readonly Dictionary<DocumentId, Doc> _docs = new();

    public event EventHandler<DocumentChangedEventArgs> DocumentChanged = delegate { };
    public event EventHandler<DocumentInfo> DocumentOpened = delegate { };
    public event EventHandler<DocumentInfo> DocumentClosed = delegate { };
    public event EventHandler<DocumentInfo> DocumentStateChanged = delegate { };
    public event EventHandler<ExternalChangeEventArgs> ExternalChange = delegate { };

    /// <summary>Crée un document et lève DocumentOpened, comme le vrai éditeur.</summary>
    public DocumentInfo Create(string text, string languageId, string? path = null)
    {
        var id = DocumentId.New();
        _docs[id] = new Doc(text, path, languageId);
        var info = GetInfo(id);
        DocumentOpened(this, info);
        return info;
    }

    public DocumentInfo CreateDocument() => Create("", "plaintext");

    public DocumentInfo GetInfo(DocumentId id)
    {
        var d = _docs[id];
        return new DocumentInfo(id, d.FilePath, d.LanguageId, false);
    }

    public IReadOnlyList<DocumentInfo> GetOpenDocuments() => _docs.Keys.Select(GetInfo).ToList();

    public string GetText(DocumentId id) => _docs[id].Text;
    public int GetLineCount(DocumentId id) => _docs[id].Text.Split('\n').Length;

    public string GetLineText(DocumentId id, int line) => _docs[id].Text.Split('\n')[line];

    public void ApplyEdit(DocumentId id, TextEdit edit)
    {
        var d = _docs[id];
        var start = Offset(d.Text, edit.Range.Start);
        var end = Offset(d.Text, edit.Range.End);
        d.Text = d.Text[..start] + edit.NewText + d.Text[end..];
        DocumentChanged(this, new DocumentChangedEventArgs(id, [edit]));
    }

    public Task SaveAsAsync(DocumentId id, string path, CancellationToken ct = default)
    {
        var d = _docs[id];
        d.FilePath = path;
        d.LanguageId = System.IO.Path.GetExtension(path).ToLowerInvariant() == ".py" ? "python" : "plaintext";
        DocumentStateChanged(this, GetInfo(id));
        return Task.CompletedTask;
    }

    public bool CloseDocument(DocumentId id)
    {
        if (!_docs.ContainsKey(id)) return false;
        var info = GetInfo(id);
        _docs.Remove(id);
        DocumentClosed(this, info);
        return true;
    }

    private static int Offset(string text, TextPosition p)
    {
        var offset = 0;
        for (var line = 0; line < p.Line; line++) offset = text.IndexOf('\n', offset) + 1;
        return offset + p.Column;
    }

    // Non utilisé dans les tests
    public Task<DocumentInfo> OpenDocumentAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
    public void ApplyEdits(DocumentId id, IReadOnlyList<TextEdit> edits) => throw new NotSupportedException();
    public CaretSet GetCaretSet(DocumentId id) => throw new NotSupportedException();
    public void SetCaretSet(DocumentId id, CaretSet carets) => throw new NotSupportedException();
    public void Undo(DocumentId id) => throw new NotSupportedException();
    public void Redo(DocumentId id) => throw new NotSupportedException();
    public Task SaveAsync(DocumentId id, CancellationToken ct = default) => Task.CompletedTask;
    public FileEncoding GetEncoding(DocumentId id) => throw new NotSupportedException();
    public void SetEncoding(DocumentId id, FileEncoding encoding) => throw new NotSupportedException();
    public Task CheckExternalChangeAsync(DocumentId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReloadFromDiskAsync(DocumentId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task KeepLocalVersionAsync(DocumentId id, CancellationToken ct = default) => Task.CompletedTask;
}