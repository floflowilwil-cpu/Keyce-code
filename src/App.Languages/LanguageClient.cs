using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace App.Languages;

/// <summary>Client LSP : démarre un serveur, ouvre des documents, reçoit les diagnostics.</summary>
public sealed class LanguageClient : IDisposable
{
    private readonly LanguageServerProcess _server = new();

    /// <summary>Déclenché quand le serveur publie les diagnostics d'un document.</summary>
    public event Action<Uri, Diagnostic[]>? DiagnosticsReceived;

    public async Task StartAsync(string command, string arguments, Uri rootUri)
    {
        _server.Start(command, arguments, new Receiver(this));

        var initParams = new InitializeParams
        {
            ProcessId = Environment.ProcessId,
            RootUri = rootUri,
            Capabilities = new ClientCapabilities()
        };

        await _server.Rpc!.InvokeWithParameterObjectAsync<InitializeResult>("initialize", initParams);
        await _server.Rpc.NotifyWithParameterObjectAsync("initialized", new { });
    }

    public Task OpenDocumentAsync(Uri uri, string languageId, string text, int version = 1)
    {
        var p = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = languageId,
                Version = version,
                Text = text
            }
        };
        return _server.Rpc!.NotifyWithParameterObjectAsync("textDocument/didOpen", p);
    }

    public async Task StopAsync()
    {
        if (_server.Rpc is null) return;
        await _server.Rpc.InvokeAsync<object?>("shutdown");
        await _server.Rpc.NotifyAsync("exit");
    }

    public void Dispose() => _server.Dispose();

    // Objet qui reçoit les messages envoyés par le serveur.
    private sealed class Receiver(LanguageClient owner)
    {
        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void OnPublishDiagnostics(PublishDiagnosticParams p)
            => owner.DiagnosticsReceived?.Invoke(p.Uri, p.Diagnostics);
    }
}