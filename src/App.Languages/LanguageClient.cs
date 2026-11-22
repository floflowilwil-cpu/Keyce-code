using System.Collections.Concurrent;
using System.ComponentModel;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Lsp = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace App.Languages;

/// <summary>Client LSP : un serveur par langage. Ne lève que des AppException.</summary>
public sealed partial class LanguageClient : ILanguageClient, IDisposable
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

             private static readonly Dictionary<string, (string Command, string Arguments)> Servers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = ("csharp-ls", ""),
            ["python"] = ("python", "-m pylsp"),
            ["json"] = ("cmd.exe", "/c vscode-json-language-server --stdio"),
            ["html"] = ("cmd.exe", "/c vscode-html-language-server --stdio"),
            ["css"] = ("cmd.exe", "/c vscode-css-language-server --stdio"),
            ["javascript"] = ("cmd.exe", "/c typescript-language-server --stdio"),
            ["typescript"] = ("cmd.exe", "/c typescript-language-server --stdio"),
        };

    private static readonly Dictionary<string, string> Extensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "csharp",
            [".py"] = "python",
            [".json"] = "json",
            [".html"] = "html",
            [".htm"] = "html",
            [".css"] = "css",
            [".js"] = "javascript",
            [".mjs"] = "javascript",
            [".ts"] = "typescript",
        };

    private readonly ConcurrentDictionary<string, LanguageServerProcess> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Uri, int> _versions = new();
    private readonly ConcurrentDictionary<Uri, string> _openLanguages = new();   // document ouvert -> langage
    private readonly ConcurrentDictionary<string, bool> _incrementalSync = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public event EventHandler<DiagnosticsPublishedEventArgs> DiagnosticsPublished = delegate { };

    /// <summary>Un serveur s'est arrêté ou a planté. Argument = langage.</summary>
    public event EventHandler<string>? ServerDisconnected;

    /// <summary>Un serveur existe-t-il pour ce langage ?</summary>
   public static bool SupportsLanguage(string languageId) => Servers.ContainsKey(ServerKey(languageId));

        /// <summary>Capacités déclarées au serveur : notre client sait recevoir des diagnostics.</summary>
    private static Lsp.ClientCapabilities CreateCapabilities()
        => JObject.Parse("""{"textDocument":{"publishDiagnostics":{"relatedInformation":true}}}""")
            .ToObject<Lsp.ClientCapabilities>()!;

    // ---------- Contrat ILanguageClient ----------

    public async Task StartServerAsync(string languageId, string workspaceRoot, CancellationToken ct = default)
    {
        languageId = ServerKey(languageId); 
        if (!Servers.TryGetValue(languageId, out var config))
            throw new AppException(ErrorCode.InvalidArgument,
                $"Aucun serveur configuré pour le langage '{languageId}'.");

        await _startLock.WaitAsync(ct);
        try
        {
            if (_servers.ContainsKey(languageId)) return;   // déjà démarré et vivant
            var server = new LanguageServerProcess();
            try
            {
                try
                {
                    server.Start(config.Command, config.Arguments, new Receiver(this));
                }
                catch (Win32Exception e)
                {
                    throw new AppException(ErrorCode.Unavailable,
                        $"Impossible de lancer le serveur '{languageId}' (commande '{config.Command} {config.Arguments}'). " +
                        "Vérifie que le langage et son serveur sont installés.", e);
                }

                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)) + Path.DirectorySeparatorChar;
                var initParams = new Lsp.InitializeParams
               {
                  ProcessId = Environment.ProcessId,
                  RootUri = new Uri(root),
                  Capabilities = CreateCapabilities()
                };

                var init = await InvokeAsync<JToken?>(server.Rpc!, "initialize", initParams, InitializeTimeout, ct);
                _incrementalSync[languageId] = SyncKind(init) == 2;

                await NotifyAsync(server.Rpc!, "initialized", new { });

                // Si le serveur tombe plus tard, on le retire pour permettre un redémarrage.
                server.Rpc!.Disconnected += (_, _) => OnServerDisconnected(languageId, server);
                _servers[languageId] = server;
            }
            catch
            {
                server.Dispose();   // jamais de processus orphelin
                throw;
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<IReadOnlyList<CompletionItem>> CompletionAsync(
        string path, TextPosition position, CancellationToken ct = default)
    {
        var p = new Lsp.CompletionParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = ToUri(path) },
            Position = new Lsp.Position(position.Line, position.Column)
        };

        var result = await InvokeAsync<JToken?>(GetRpc(path), "textDocument/completion", p, RequestTimeout, ct);

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

        var result = await InvokeAsync<JToken?>(GetRpc(path), "textDocument/definition", p, RequestTimeout, ct);

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
        var rpc = GetServerRpc(languageId);

        _openLanguages[uri] = languageId;
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
        return NotifyAsync(rpc, "textDocument/didOpen", p);
    }

    /// <summary>Envoie le nouveau texte complet du document au serveur.</summary>
    public Task ChangeDocumentAsync(string path, string newText)
        => ChangeDocumentAsync(path, [], () => newText, forceFullText: true);

    /// <summary>
    /// Envoie des modifications dans l'ordre où elles ont été appliquées (chacune dans l'état
    /// obtenu après la précédente). Si le serveur ne gère pas le mode incrémental, envoie le texte complet.
    /// </summary>
    public Task ChangeDocumentAsync(string path, IReadOnlyList<TextEdit> edits, Func<string> fullText)
        => ChangeDocumentAsync(path, edits, fullText, forceFullText: false);

    private Task ChangeDocumentAsync(string path, IReadOnlyList<TextEdit> edits, Func<string> fullText, bool forceFullText)
    {
        var uri = ToUri(path);
        var rpc = GetRpc(path);

        if (!_versions.TryGetValue(uri, out var version))
            throw new AppException(ErrorCode.InvalidArgument,
                "Le document n'est pas ouvert : appelle OpenDocumentAsync d'abord.");

        _versions[uri] = ++version;

        var incremental = !forceFullText
            && _openLanguages.TryGetValue(uri, out var languageId)
            && _incrementalSync.TryGetValue(ServerKey(languageId), out var supported)
             && supported;
        var changes = incremental
            ? edits.Select(e => new Lsp.TextDocumentContentChangeEvent
              {
                  Range = ToLspRange(e.Range),
                  Text = e.NewText
              }).ToArray()
            : new[] { new Lsp.TextDocumentContentChangeEvent { Text = fullText() } };

        var p = new Lsp.DidChangeTextDocumentParams
        {
            TextDocument = new Lsp.VersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = changes
        };
        return NotifyAsync(rpc, "textDocument/didChange", p);
    }

    public Task CloseDocumentAsync(string path)
    {
        var uri = ToUri(path);
        var rpc = GetRpc(path);

        _versions.TryRemove(uri, out _);
        _openLanguages.TryRemove(uri, out _);

        var p = new Lsp.DidCloseTextDocumentParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = uri }
        };
        return NotifyAsync(rpc, "textDocument/didClose", p);
    }

    public async Task<string?> GetHoverAsync(string path, TextPosition position, CancellationToken ct = default)
    {
        var p = new Lsp.TextDocumentPositionParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = ToUri(path) },
            Position = new Lsp.Position(position.Line, position.Column)
        };

        var result = await InvokeAsync<JToken?>(GetRpc(path), "textDocument/hover", p, RequestTimeout, ct);
        return ExtractText(result?["contents"]);
    }

    /// <summary>Arrête proprement tous les serveurs démarrés. Ne lève jamais d'erreur.</summary>
    public async Task StopAsync()
    {
        foreach (var (language, server) in _servers.ToArray())
        {
            // On retire le serveur d'abord : une fermeture voulue n'est pas une panne.
            _servers.TryRemove(language, out _);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await server.Rpc!.InvokeWithCancellationAsync<object?>("shutdown", cancellationToken: cts.Token);
                await server.Rpc.NotifyAsync("exit");
            }
            catch
            {
                // Serveur déjà tombé ou trop lent : Dispose() le tue.
            }
            server.Dispose();
        }
        _versions.Clear();
        _openLanguages.Clear();
    }

    public void Dispose()
    {
        foreach (var server in _servers.Values) server.Dispose();
        _servers.Clear();
        _startLock.Dispose();
    }

    // ---------- Gestion des erreurs ----------

    private void OnServerDisconnected(string languageId, LanguageServerProcess server)
    {
        // On ne retire que si c'est bien CE serveur (pas un redémarrage plus récent).
        if (_servers.TryRemove(new KeyValuePair<string, LanguageServerProcess>(languageId, server)))
            ServerDisconnected?.Invoke(this, languageId);
    }

    private static async Task<T> InvokeAsync<T>(
        JsonRpc rpc, string method, object parameters, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            return await rpc.InvokeWithParameterObjectAsync<T>(method, parameters, cancellationToken: cts.Token);
        }
        catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException or IOException)
        {
            throw new AppException(ErrorCode.Unavailable, "Le serveur de langage est arrêté ou ne répond plus.", e);
        }
        catch (OperationCanceledException e) when (ct.IsCancellationRequested)
        {
            throw new AppException(ErrorCode.Cancelled, "La requête a été annulée.", e);
        }
        catch (OperationCanceledException e)
        {
            throw new AppException(ErrorCode.Timeout,
                $"Le serveur n'a pas répondu en {timeout.TotalSeconds:0} secondes ({method}).", e);
        }
        catch (RemoteInvocationException e)
        {
            throw new AppException(ErrorCode.Unknown, $"Le serveur a renvoyé une erreur : {e.Message}", e);
        }
    }

    private static async Task NotifyAsync(JsonRpc rpc, string method, object parameters)
    {
        try
        {
            await rpc.NotifyWithParameterObjectAsync(method, parameters);
        }
        catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException or IOException)
        {
            throw new AppException(ErrorCode.Unavailable, "Le serveur de langage est arrêté ou ne répond plus.", e);
        }
    }

    // ---------- Outils internes ----------

    /// <summary>Canal du serveur qui gère ce fichier : par son langage s'il est ouvert, sinon par son extension.</summary>
    private JsonRpc GetRpc(string path)
    {
        if (_openLanguages.TryGetValue(ToUri(path), out var openLanguage))
            return GetServerRpc(openLanguage);

        var ext = Path.GetExtension(path);
        if (!Extensions.TryGetValue(ext, out var languageId))
            throw new AppException(ErrorCode.InvalidArgument, $"Type de fichier non pris en charge : '{ext}'.");

        return GetServerRpc(languageId);
    }

    private JsonRpc GetServerRpc(string languageId)
    {
        languageId = ServerKey(languageId);
        if (_servers.TryGetValue(languageId, out var server) && server.Rpc is { } rpc)
            return rpc;

        if (!Servers.ContainsKey(languageId))
            throw new AppException(ErrorCode.InvalidArgument, $"Langage non pris en charge : '{languageId}'.");

        throw new AppException(ErrorCode.Unavailable,
            $"Le serveur '{languageId}' n'est pas démarré : appelle StartServerAsync d'abord.");
    }

    // Les documents sans fichier utilisent un chemin « untitled:Untitled-1 » (convention LSP).
    private static Uri ToUri(string path)
        => path.StartsWith("untitled:", StringComparison.OrdinalIgnoreCase)
            ? new Uri(path)
            : new Uri(Path.GetFullPath(path));

    private static string FromUri(Uri uri) => uri.IsFile ? uri.LocalPath : uri.OriginalString;

    // textDocumentSync : un nombre (0 aucun, 1 texte complet, 2 incrémental) ou un objet { change }.
    private static int SyncKind(JToken? initializeResult)
    {
        var sync = initializeResult?["capabilities"]?["textDocumentSync"];
        return sync?.Type switch
        {
            JTokenType.Integer => (int)sync!,
            JTokenType.Object => (int?)sync!["change"] ?? 0,
            _ => 0
        };
    }

    private static Lsp.Range ToLspRange(TextRange r) => new()
    {
        Start = new Lsp.Position(r.Start.Line, r.Start.Column),
        End = new Lsp.Position(r.End.Line, r.End.Column)
    };

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

    private sealed class Receiver(LanguageClient owner)
    {
        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void OnPublishDiagnostics(Lsp.PublishDiagnosticParams p)
            => owner.Raise(FromUri(p.Uri), (p.Diagnostics ?? []).Select(Map).ToList());
    }
}