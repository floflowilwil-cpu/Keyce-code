using App.Core.Contracts;
using App.Core.Models;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Lsp = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace App.Languages;

/// <summary>Client LSP : implémente le contrat ILanguageClient de l'équipe.</summary>
public sealed class LanguageClient : ILanguageClient, IDisposable
{
    // Serveurs configurés par langage. On en ajoutera d'autres ici (C#, JS/TS...).
    private static readonly Dictionary<string, (string Command, string Arguments)> Servers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["python"] = ("python", "-m pylsp"),
        };

    private readonly LanguageServerProcess _server = new();
    private readonly Dictionary<Uri, int> _versions = new();

    public event EventHandler<DiagnosticsPublishedEventArgs> DiagnosticsPublished = delegate { };

    // ---------- Contrat ILanguageClient ----------

    public async Task StartServerAsync(string languageId, string workspaceRoot, CancellationToken ct = default)
    {
        if (!Servers.TryGetValue(languageId, out var config))
            throw new NotSupportedException($"Aucun serveur configuré pour le langage '{languageId}'.");

        _server.Start(config.Command, config.Arguments, new Receiver(this));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)) + Path.DirectorySeparatorChar;
        var initParams = new Lsp.InitializeParams
        {
            ProcessId = Environment.ProcessId,
            RootUri = new Uri(root),
            Capabilities = new Lsp.ClientCapabilities()
        };

        await _server.Rpc!.InvokeWithParameterObjectAsync<Lsp.InitializeResult>(
            "initialize", initParams, cancellationToken: ct);
        await _server.Rpc.NotifyWithParameterObjectAsync("initialized", new { });
    }

    public async Task<IReadOnlyList<CompletionItem>> CompletionAsync(
        string path, TextPosition position, CancellationToken ct = default)
    {
        var p = new Lsp.CompletionParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = ToUri(path) },
            Position = new Lsp.Position(position.Line, position.Column)
        };

        var result = await _server.Rpc!.InvokeWithParameterObjectAsync<JToken?>(
            "textDocument/completion", p, cancellationToken: ct);

        // Le serveur peut répondre par un tableau ou par un objet { items: [...] }
        JArray? items = result switch
        {
            JArray array => array,
            JObject obj => obj["items"] as JArray,
            _ => null
        };

        if (items is null) return [];

        return items
            .Select(i =>
            {
                var label = i["label"]?.ToString() ?? "";
                var insertText = i["insertText"]?.ToString() ?? label;
                string? kind = i["kind"]?.Type == JTokenType.Integer
                    ? ((Lsp.CompletionItemKind)(int)i["kind"]!).ToString()
                    : null;
                return new CompletionItem(label, insertText, kind, i["detail"]?.ToString());
            })
            .Where(c => c.Label.Length > 0)
            .ToList();
    }

    public async Task<IReadOnlyList<SourceLocation>> DefinitionAsync(
        string path, TextPosition position, CancellationToken ct = default)
    {
        var p = new Lsp.TextDocumentPositionParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = ToUri(path) },
            Position = new Lsp.Position(position.Line, position.Column)
        };

        var result = await _server.Rpc!.InvokeWithParameterObjectAsync<JToken?>(
            "textDocument/definition", p, cancellationToken: ct);

        // Réponse possible : un Location, une liste de Location ou une liste de LocationLink
        var nodes = new List<JObject>();
        if (result is JArray array) nodes.AddRange(array.OfType<JObject>());
        else if (result is JObject single) nodes.Add(single);

        var locations = new List<SourceLocation>();
        foreach (var n in nodes)
        {
            var uriText = (n["uri"] ?? n["targetUri"])?.ToString();
            var range = n["range"] ?? n["targetSelectionRange"];
            if (uriText is null || range is null) continue;
            locations.Add(new SourceLocation(new Uri(uriText).LocalPath, ParseRange(range)));
        }
        return locations;
    }

    // ---------- Méthodes supplémentaires (à proposer pour le contrat) ----------

    public Task OpenDocumentAsync(string path, string languageId, string text, int version = 1)
    {
        var uri = ToUri(path);
        _versions[uri] = version;

        var p = new Lsp.DidOpenTextDocumentParams
        {
            TextDocument = new Lsp.TextDocumentItem
            {
                Uri = uri,
                LanguageId = languageId,
                Version = version,
                Text = text
            }
        };
        return _server.Rpc!.NotifyWithParameterObjectAsync("textDocument/didOpen", p);
    }

    /// <summary>Envoie le nouveau texte complet du document au serveur.</summary>
    public Task ChangeDocumentAsync(string path, string newText)
    {
        var uri = ToUri(path);
        if (!_versions.TryGetValue(uri, out var version))
            throw new InvalidOperationException("Le document n'est pas ouvert : appelle OpenDocumentAsync d'abord.");

        _versions[uri] = ++version;

        var p = new Lsp.DidChangeTextDocumentParams
        {
            TextDocument = new Lsp.VersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = [new Lsp.TextDocumentContentChangeEvent { Text = newText }]
        };
        return _server.Rpc!.NotifyWithParameterObjectAsync("textDocument/didChange", p);
    }

    public Task CloseDocumentAsync(string path)
    {
        var uri = ToUri(path);
        _versions.Remove(uri);

        var p = new Lsp.DidCloseTextDocumentParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = uri }
        };
        return _server.Rpc!.NotifyWithParameterObjectAsync("textDocument/didClose", p);
    }

    public async Task<string?> GetHoverAsync(string path, TextPosition position, CancellationToken ct = default)
    {
        var p = new Lsp.TextDocumentPositionParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = ToUri(path) },
            Position = new Lsp.Position(position.Line, position.Column)
        };

        var result = await _server.Rpc!.InvokeWithParameterObjectAsync<JToken?>(
            "textDocument/hover", p, cancellationToken: ct);
        return ExtractText(result?["contents"]);
    }

    public async Task StopAsync()
    {
        if (_server.Rpc is null) return;
        await _server.Rpc.InvokeAsync<object?>("shutdown");
        await _server.Rpc.NotifyAsync("exit");
    }

    public void Dispose() => _server.Dispose();

    // ---------- Outils internes ----------

    private static Uri ToUri(string path) => new(Path.GetFullPath(path));

    private static TextRange ParseRange(JToken r) => new(
        new TextPosition((int)r["start"]!["line"]!, (int)r["start"]!["character"]!),
        new TextPosition((int)r["end"]!["line"]!, (int)r["end"]!["character"]!));

    private static string? ExtractText(JToken? token) => token switch
    {
        JArray array => string.Join("\n", array.Select(ExtractText).Where(s => !string.IsNullOrWhiteSpace(s))),
        JObject obj => obj["value"]?.ToString(),
        JValue value => value.ToString(),
        _ => null
    };

    private static Diagnostic Map(Lsp.Diagnostic d) => new(
        new TextRange(
            new TextPosition(d.Range.Start.Line, d.Range.Start.Character),
            new TextPosition(d.Range.End.Line, d.Range.End.Character)),
        d.Severity switch
        {
            Lsp.DiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            Lsp.DiagnosticSeverity.Information => DiagnosticSeverity.Information,
            Lsp.DiagnosticSeverity.Hint => DiagnosticSeverity.Hint,
            _ => DiagnosticSeverity.Error
        },
        d.Message,
        d.Source);

    private void Raise(string path, IReadOnlyList<Diagnostic> diagnostics)
        => DiagnosticsPublished(this, new DiagnosticsPublishedEventArgs(path, diagnostics));

    // Objet qui reçoit les messages envoyés par le serveur.
    private sealed class Receiver(LanguageClient owner)
    {
        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void OnPublishDiagnostics(Lsp.PublishDiagnosticParams p)
            => owner.Raise(p.Uri.LocalPath, (p.Diagnostics ?? []).Select(Map).ToList());
    }
}