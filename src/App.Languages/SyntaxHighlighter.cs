using System.Collections.Concurrent;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Languages;

/// <summary>Colorateur : suit les documents de l'éditeur et tient leurs jetons à jour.</summary>
public sealed class SyntaxHighlighter : ISyntaxHighlighter, IDisposable
{
    private sealed record Tracked(string LanguageId, DocumentTokenizer Tokenizer);

    private readonly IEditorService _editor;
    private readonly GrammarProvider _grammars;
    private readonly ConcurrentDictionary<DocumentId, Tracked> _documents = new();

    public event EventHandler<TokensInvalidatedEventArgs> TokensInvalidated = delegate { };

    public SyntaxHighlighter(IEditorService editor, GrammarProvider? grammars = null)
    {
        _editor = editor;
        _grammars = grammars ?? new GrammarProvider();

        // On s'abonne AVANT de lire GetOpenDocuments() pour ne rater aucun document.
        _editor.DocumentOpened += OnDocumentOpened;
        _editor.DocumentClosed += OnDocumentClosed;
        _editor.DocumentChanged += OnDocumentChanged;
        _editor.DocumentStateChanged += OnDocumentStateChanged;

        foreach (var info in _editor.GetOpenDocuments()) Track(info);
    }

    public IReadOnlyList<SyntaxToken> GetTokens(DocumentId documentId, int line)
        => _documents.TryGetValue(documentId, out var t) ? t.Tokenizer.GetTokens(line) : Array.Empty<SyntaxToken>();

    public void Dispose()
    {
        _editor.DocumentOpened -= OnDocumentOpened;
        _editor.DocumentClosed -= OnDocumentClosed;
        _editor.DocumentChanged -= OnDocumentChanged;
        _editor.DocumentStateChanged -= OnDocumentStateChanged;
    }

    private void OnDocumentOpened(object? sender, DocumentInfo info) => Track(info);

    private void OnDocumentClosed(object? sender, DocumentInfo info) => _documents.TryRemove(info.Id, out _);

    // Appelé aussi quand seul l'état « modifié » change : on ne refait quelque chose
    // que si le langage a changé (ex. « Enregistrer sous » un .py).
    private void OnDocumentStateChanged(object? sender, DocumentInfo info) => Track(info);

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (!_documents.TryGetValue(e.Id, out var tracked)) return;

        (int First, int Last) range;
        try
        {
            range = tracked.Tokenizer.ApplyEdits(e.Edits);
        }
        catch (Exception)
        {
            // Nos modifications ne collent plus au texte réel : on repart de zéro.
            try { range = tracked.Tokenizer.SetText(_editor.GetText(e.Id)); }
            catch (AppException) { return; }
        }

        if (range.Last >= range.First) Raise(e.Id, range);
    }

    private void Track(DocumentInfo info)
    {
        var grammar = _grammars.Get(info.LanguageId);

        if (grammar is null)
        {
            // Langage sans coloration : on libère la mémoire et on efface les anciennes couleurs.
            if (_documents.TryRemove(info.Id, out var old))
                Raise(info.Id, (0, old.Tokenizer.LineCount - 1));
            return;
        }

        if (_documents.TryGetValue(info.Id, out var existing) && existing.LanguageId == info.LanguageId)
            return;

        string text;
        try { text = _editor.GetText(info.Id); }
        catch (AppException) { return; }   // document fermé entre-temps

        var tokenizer = new DocumentTokenizer(grammar);
        var range = tokenizer.SetText(text);
        _documents[info.Id] = new Tracked(info.LanguageId, tokenizer);
        Raise(info.Id, range);
    }

    private void Raise(DocumentId id, (int First, int Last) range)
        => TokensInvalidated(this, new TokensInvalidatedEventArgs(id, range.First, range.Last));
}