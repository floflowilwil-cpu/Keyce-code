using App.Core.Models;
using Newtonsoft.Json.Linq;
using Lsp = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace App.Languages;

// Fonctions supplémentaires, hors contrat ILanguageClient (à proposer à l'équipe).
public sealed partial class LanguageClient
{
    public async Task<IReadOnlyList<SourceLocation>> ReferencesAsync(
        string path, TextPosition position, bool includeDeclaration = true, CancellationToken ct = default)
    {
        var p = new Lsp.ReferenceParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = ToUri(path) },
            Position = new Lsp.Position(position.Line, position.Column),
            Context = new Lsp.ReferenceContext { IncludeDeclaration = includeDeclaration }
        };

        var result = await InvokeAsync<JToken?>(GetRpc(path), "textDocument/references", p, RequestTimeout, ct);
        if (result is not JArray array) return [];

        var locations = new List<SourceLocation>();
        foreach (var n in array.OfType<JObject>())
        {
            var uriText = n["uri"]?.ToString();
            if (uriText is null || n["range"] is null) continue;
            locations.Add(new SourceLocation(new Uri(uriText).LocalPath, ParseRange(n["range"]!)));
        }
        return locations;
    }

    /// <summary>
    /// Renommage : modifications à appliquer, par fichier. Dans chaque liste, elles sont triées
    /// de la fin vers le début : on peut les appliquer dans l'ordre sans décaler les suivantes.
    /// Dictionnaire vide si le serveur ne sait pas renommer ici.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameAsync(
        string path, TextPosition position, string newName, CancellationToken ct = default)
    {
        var p = new Lsp.RenameParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = ToUri(path) },
            Position = new Lsp.Position(position.Line, position.Column),
            NewName = newName
        };

        var result = await InvokeAsync<JToken?>(GetRpc(path), "textDocument/rename", p, RequestTimeout, ct);
        var edits = new Dictionary<string, IReadOnlyList<TextEdit>>(StringComparer.OrdinalIgnoreCase);
        if (result is not JObject workspaceEdit) return edits;

        // Forme 1 : { changes: { uri: [TextEdit, ...] } }
        if (workspaceEdit["changes"] is JObject changes)
            foreach (var prop in changes.Properties())
                edits[new Uri(prop.Name).LocalPath] = ParseEdits(prop.Value);

        // Forme 2 : { documentChanges: [ { textDocument: { uri }, edits: [...] } ] }
        // (les opérations sur fichiers : créer, renommer, supprimer, sont ignorées)
        if (workspaceEdit["documentChanges"] is JArray documentChanges)
            foreach (var change in documentChanges.OfType<JObject>())
            {
                var uriText = change["textDocument"]?["uri"]?.ToString();
                if (uriText is null || change["edits"] is not JArray) continue;
                edits[new Uri(uriText).LocalPath] = ParseEdits(change["edits"]);
            }

        return edits;
    }

    /// <summary>Formatage du document entier. Modifications triées de la fin vers le début.</summary>
    public async Task<IReadOnlyList<TextEdit>> FormatDocumentAsync(
        string path, int tabSize = 4, bool insertSpaces = true, CancellationToken ct = default)
    {
        var p = new Lsp.DocumentFormattingParams
        {
            TextDocument = new Lsp.TextDocumentIdentifier { Uri = ToUri(path) },
            Options = new Lsp.FormattingOptions { TabSize = tabSize, InsertSpaces = insertSpaces }
        };

        var result = await InvokeAsync<JToken?>(GetRpc(path), "textDocument/formatting", p, RequestTimeout, ct);
        return ParseEdits(result);
    }

    private static IReadOnlyList<TextEdit> ParseEdits(JToken? token)
    {
        if (token is not JArray array) return [];

        return array.OfType<JObject>()
            .Where(e => e["range"] is not null)
            .Select(e => new TextEdit(ParseRange(e["range"]!), e["newText"]?.ToString() ?? ""))
            .OrderByDescending(e => e.Range.Start.Line)
            .ThenByDescending(e => e.Range.Start.Column)
            .ToList();
    }
}