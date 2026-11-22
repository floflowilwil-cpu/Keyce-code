using App.Core.Errors;
using App.Core.Models;
using App.Languages;

static class TypeScriptTest
{
    public static async Task RunAsync(string language, string extension, string badCode, string goodCode)
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
            Console.WriteLine($"Démarrage du serveur {language} (tsserver peut mettre 10 à 20 secondes)...");
            await client.StartServerAsync(language, root);
        }
        catch (AppException e)
        {
            Console.WriteLine($"ERREUR {e.Code} : {e.Message}");
            return;
        }

        var file = Path.Combine(root, "test" + extension);

        Console.WriteLine("--- Code avec une erreur ---");
        await client.OpenDocumentAsync(file, language, badCode);
        if (!await signal.WaitAsync(TimeSpan.FromSeconds(30)))
            Console.WriteLine("Timeout : aucun diagnostic reçu");

        Console.WriteLine("--- Après correction ---");
        await client.ChangeDocumentAsync(file, goodCode);
        if (!await signal.WaitAsync(TimeSpan.FromSeconds(15)))
            Console.WriteLine("Timeout : aucun nouveau diagnostic");

        Console.WriteLine("--- Complétion après 'console.' ---");
        await client.ChangeDocumentAsync(file, "console.\n");
        await Task.Delay(1500);
        var items = await client.CompletionAsync(file, new TextPosition(0, 8));
        Console.WriteLine($"{items.Count} suggestion(s) : {string.Join(", ", items.Take(8).Select(i => i.Label))}");

        await client.CloseDocumentAsync(file);
        await client.StopAsync();
        Console.WriteLine("Terminé.");
    }

    public static Task RunJavaScriptAsync() => RunAsync(
        "javascript", ".js",
        "const x = ;\n",
        "const x = 1;\n");

    public static Task RunTypeScriptAsync() => RunAsync(
        "typescript", ".ts",
        "const n: number = \"texte\";\n",
        "const n: number = 3;\n");
}