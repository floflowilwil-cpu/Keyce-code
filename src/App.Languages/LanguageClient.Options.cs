using App.Core.Errors;

namespace App.Languages;

public sealed partial class LanguageClient
{
    /// <summary>
    /// Options envoyées au serveur dans « initialize ». Le serveur TypeScript a besoin du chemin
    /// de tsserver.js quand TypeScript est installé globalement (npm install -g typescript@6).
    /// Chemin personnalisable avec la variable d'environnement KEYCE_TSSERVER_PATH.
    /// </summary>
    private static object? GetInitializationOptions(string languageId)
    {
        if (!languageId.Equals("javascript", StringComparison.OrdinalIgnoreCase)
            && !languageId.Equals("typescript", StringComparison.OrdinalIgnoreCase))
            return null;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("KEYCE_TSSERVER_PATH"),
            Path.Combine(appData, "npm", "node_modules", "typescript", "lib", "tsserver.js"),
        };

        var path = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (path is null)
            throw new AppException(ErrorCode.Unavailable,
                "TypeScript est introuvable. Installe-le avec 'npm install -g typescript@6' " +
                "ou indique son chemin dans la variable d'environnement KEYCE_TSSERVER_PATH.");

        return new { tsserver = new { path } };
    }
}