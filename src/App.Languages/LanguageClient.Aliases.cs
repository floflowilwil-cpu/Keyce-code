namespace App.Languages;

public sealed partial class LanguageClient
{
    // Langages qui partagent le serveur d'un autre langage.
    private static readonly Dictionary<string, string> ServerKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["javascript"] = "typescript",
            ["javascriptreact"] = "typescript",
            ["typescriptreact"] = "typescript",
            ["jsonc"] = "json",
            ["scss"] = "css",
            ["less"] = "css",
        };

    /// <summary>Nom du serveur qui gère ce langage (souvent le langage lui-même).</summary>
    public static string ServerKey(string languageId)
        => ServerKeys.TryGetValue(languageId, out var key) ? key : languageId;

    /// <summary>Serveurs actuellement démarrés (pour les tests et le diagnostic).</summary>
    public IReadOnlyCollection<string> RunningServers => _servers.Keys.ToArray();
}