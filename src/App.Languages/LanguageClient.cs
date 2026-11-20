using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace App.Languages;

/// <summary>Client LSP : démarre un serveur, ouvre des documents, reçoit les diagnostics.</summary>
public sealed class LanguageClient : IDisposable
{
    private readonly LanguageServerProcess _server = new();
    private readonly Dictionary<Uri, int> _versions = new();   // <-- ajout étape 7 (haut de la classe)

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

        _versions[uri] = version;                               // <-- ajout étape 7
        return _server.Rpc!.NotifyWithParameterObjectAsync("textDocument/didOpen", p);
    }

    /// <summary>Envoie le nouveau texte complet du document au serveur.</summary>
    public Task ChangeDocumentAsync(Uri uri, string newText)
    {
        if (!_versions.TryGetValue(uri, out var version))
            throw new InvalidOperationException("Le document n'est pas ouvert : appelle OpenDocumentAsync d'abord.");

        _versions[uri] = ++version;

        var p = new DidChangeTextDocumentParams
        {
            TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = [new TextDocumentContentChangeEvent { Text = newText }]
        };
        return _server.Rpc!.NotifyWithParameterObjectAsync("textDocument/didChange", p);
    }

    /// <summary>Prévient le serveur que le document est fermé.</summary>
    public Task CloseDocumentAsync(Uri uri)
    {
        _versions.Remove(uri);

        var p = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri }
        };
        return _server.Rpc!.NotifyWithParameterObjectAsync("textDocument/didClose", p);
    }

    public async Task<IReadOnlyList<CompletionEntry>> GetCompletionsAsync(Uri uri, int line, int character)
    {
        var p = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position = new Position(line, character)
        };

        var result = await _server.Rpc!.InvokeWithParameterObjectAsync<JToken?>("textDocument/completion", p);

        JArray? items = result switch
        {
            JArray array => array,
            JObject obj => obj["items"] as JArray,
            _ => null
        };

        if (items is null) return [];

        return items
            .Select(i => new CompletionEntry(i["label"]?.ToString() ?? "", i["detail"]?.ToString()))
            .Where(e => e.Label.Length > 0)
            .ToList();
    }

    public async Task<string?> GetHoverAsync(Uri uri, int line, int character)
    {
        var p = new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position = new Position(line, character)
        };

        var result = await _server.Rpc!.InvokeWithParameterObjectAsync<JToken?>("textDocument/hover", p);
        return ExtractText(result?["contents"]);
    }

    private static string? ExtractText(JToken? token) => token switch
    {
        JArray array => string.Join("\n", array.Select(ExtractText).Where(s => !string.IsNullOrWhiteSpace(s))),
        JObject obj => obj["value"]?.ToString(),
        JValue value => value.ToString(),
        _ => null
    };

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

/// <summary>Une suggestion de complétion.</summary>
public sealed record CompletionEntry(string Label, string? Detail);