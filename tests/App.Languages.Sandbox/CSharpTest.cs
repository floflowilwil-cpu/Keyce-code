using App.Core.Errors;
using App.Core.Models;
using App.Languages;

static class CSharpTest
{
    public static async Task RunAsync()
    {
        using var client = new LanguageClient();
        var signal = new SemaphoreSlim(0);

        client.DiagnosticsPublished += (_, e) =>
        {
            Console.WriteLine($"Diagnostics ({e.Diagnostics.Count}) pour {Path.GetFileName(e.Path)}");
            foreach (var d in e.Diagnostics.Take(5))
                Console.WriteLine($"  ligne {d.Range.Start.Line + 1} [{d.Severity}] : {d.Message}");
            signal.Release();
        };

        var root = Directory.GetCurrentDirectory();
        try
        {
            Console.WriteLine("Démarrage de csharp-ls (le chargement de la solution dure environ 20 secondes)...");
            await client.StartServerAsync("csharp", root);
        }
        catch (AppException e)
        {
            Console.WriteLine($"ERREUR {e.Code} : {e.Message}");
            return;
        }

        var file = Path.Combine(root, "src", "App.Core", "Models", "Common.cs");
        if (!File.Exists(file)) { Console.WriteLine($"Fichier introuvable : {file}"); return; }

        var code = File.ReadAllText(file).Replace("\r\n", "\n");

        Console.WriteLine("--- Fichier réel avec une erreur ajoutée en mémoire ---");
        await client.OpenDocumentAsync(file, "csharp", code + "\npublic class Faux { int x = \"texte\"; }\n");
        if (!await signal.WaitAsync(TimeSpan.FromSeconds(90)))
            Console.WriteLine("Timeout : aucun diagnostic reçu");

        Console.WriteLine("--- Survol sur 'TextPosition' ---");
        var hover = await client.GetHoverAsync(file, new TextPosition(3, 32));
        Console.WriteLine(hover is null ? "(aucun survol)" : hover.Length > 200 ? hover[..200] + "..." : hover);

        await client.CloseDocumentAsync(file);
        await client.StopAsync();
        Console.WriteLine("Terminé.");
    }
}