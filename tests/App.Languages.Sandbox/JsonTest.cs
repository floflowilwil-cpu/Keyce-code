using App.Core.Errors;
using App.Core.Models;
using App.Languages;

static class JsonTest
{
    public static async Task RunAsync()
    {
        using var client = new LanguageClient();
        var signal = new SemaphoreSlim(0);

        client.DiagnosticsPublished += (_, e) =>
        {
            Console.WriteLine($"Diagnostics ({e.Diagnostics.Count}) pour {Path.GetFileName(e.Path)}");
            foreach (var d in e.Diagnostics)
                Console.WriteLine($"  ligne {d.Range.Start.Line + 1} [{d.Severity}] : {d.Message}");
            signal.Release();
        };

        var root = Directory.GetCurrentDirectory();
        try
        {
            Console.WriteLine("Démarrage du serveur JSON (Node peut mettre quelques secondes)...");
            await client.StartServerAsync("json", root);
        }
        catch (AppException e)
        {
            Console.WriteLine($"ERREUR {e.Code} : {e.Message}");
            return;
        }

        var file = Path.Combine(root, "test.json");

        Console.WriteLine("--- JSON invalide ---");
        await client.OpenDocumentAsync(file, "json", "{ \"nom\": \"chat\", \"age\": , }\n");
        if (!await signal.WaitAsync(TimeSpan.FromSeconds(20)))
            Console.WriteLine("Timeout : aucun diagnostic reçu");

        Console.WriteLine("--- Après correction ---");
        await client.ChangeDocumentAsync(file, "{ \"nom\": \"chat\", \"age\": 3 }\n");
        if (!await signal.WaitAsync(TimeSpan.FromSeconds(10)))
            Console.WriteLine("Timeout : aucun diagnostic reçu");

        Console.WriteLine("--- Complétion dans un objet ---");
        var items = await client.CompletionAsync(file, new TextPosition(0, 2));
        Console.WriteLine($"{items.Count} suggestion(s) {string.Join(", ", items.Take(5).Select(i => i.Label))}");

        await client.CloseDocumentAsync(file);
        await client.StopAsync();
        Console.WriteLine("Terminé.");
    }
}