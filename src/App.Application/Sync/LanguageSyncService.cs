using System.Collections.Concurrent;
using App.Core.Contracts;
using App.Core.Models;
using App.Languages;

namespace App.Application.Sync;

/// <summary>
/// Relie l'éditeur au client LSP : didOpen / didChange / didClose sont envoyés automatiquement.
/// Sans cette classe, le serveur de langage ne voit jamais les documents et ne publie aucun diagnostic.
/// Les langages sans serveur configuré sont ignorés silencieusement.
/// </summary>
public sealed class LanguageSyncService : IDisposable
{
    private readonly IEditorService _editor;
    private readonly LanguageClient _client;
    private readonly IWorkspaceService _workspace;
    private readonly ConcurrentDictionary<DocumentId, string> _tracked = new();

    /// <summary>Message d'information ou d'erreur non bloquante (pour la sortie de l'interface).</summary>
    public event EventHandler<string>? Message;

    public LanguageSyncService(IEditorService editor, LanguageClient client, IWorkspaceService workspace)
    {
        _editor = editor;
        _client = client;
        _workspace = workspace;

        _editor.DocumentOpened += OnOpened;
        _editor.DocumentChanged += OnChanged;
        _editor.DocumentClosed += OnClosed;
    }

    private void OnOpened(object? sender, DocumentInfo info)
    {
        if (info.Path is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string root = _workspace.Current?.RootPath ?? Path.GetDirectoryName(info.Path) ?? ".";
                await _client.StartServerAsync(info.LanguageId, root);
                await _client.OpenDocumentAsync(info.Path, info.LanguageId, _editor.GetText(info.Id));
                _tracked[info.Id] = info.Path;
            }
            catch (Exception ex)
            {
                // Pas de serveur pour ce langage, ou serveur absent de la machine : ce n'est pas bloquant.
                Message?.Invoke(this, $"LSP ({info.LanguageId}) : {ex.Message}");
            }
        });
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (!_tracked.TryGetValue(e.Id, out string? path)) return;
        _ = Task.Run(async () =>
        {
            try { await _client.ChangeDocumentAsync(path, _editor.GetText(e.Id)); }
            catch (Exception ex) { Message?.Invoke(this, $"LSP : {ex.Message}"); }
        });
    }

    private void OnClosed(object? sender, DocumentInfo info)
    {
        if (!_tracked.TryRemove(info.Id, out string? path)) return;
        _ = Task.Run(async () =>
        {
            try { await _client.CloseDocumentAsync(path); }
            catch (Exception ex) { Message?.Invoke(this, $"LSP : {ex.Message}"); }
        });
    }

    public void Dispose()
    {
        _editor.DocumentOpened -= OnOpened;
        _editor.DocumentChanged -= OnChanged;
        _editor.DocumentClosed -= OnClosed;
    }
}
