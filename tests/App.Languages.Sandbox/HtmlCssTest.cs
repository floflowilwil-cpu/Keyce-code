using App.Core.Errors;
using App.Core.Models;
using App.Languages;

static class HtmlCssTest
{
    public static async Task RunHtmlAsync()
    {
        using var client = new LanguageClient();
        var root = Directory.GetCurrentDirectory();
        if (!await TryStartAsync(client, "html", root)) return;

        var file = Path.Combine(root, "test.html");

        Console.WriteLine("--- Complétion après '<di' ---");
        await client.OpenDocumentAsync(file, "html", "<di");
        await Task.Delay(1000);
        var items = await client.CompletionAsync(file, new TextPosition(0, 3));
        Console.WriteLine($"{items.Count} suggestion(s) : {string.Join(", ", items.Take(8).Select(i => i.Label))}");

        Console.WriteLine("--- Survol sur 'div' ---");
        await client.ChangeDocumentAsync(file, "<div>Bonjour</div>\n");
        await Task.Delay(500);
        var hover = await client.GetHoverAsync(file, new TextPosition(0, 2));
        Console.WriteLine(hover is null ? "(aucun survol)" : hover.Length > 200 ? hover[..200] + "..." : hover);

        await client.CloseDocumentAsync(file);
        await client.StopAsync();
        Console.WriteLine("Terminé.");
    }

    public static async Task RunCssAsync()
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
        if (!await TryStartAsync(client, "css", root)) return;

        var file = Path.Combine(root, "test.css");

        Console.WriteLine("--- CSS avec une propriété inconnue ---");
        await client.OpenDocumentAsync(file, "css", "body { colr: red; margin: 0 }\n");
        if (!await signal.WaitAsync(TimeSpan.FromSeconds(15)))
            Console.WriteLine("Timeout : aucun diagnostic reçu");

        Console.WriteLine("--- Complétion après 'co' ---");
        await client.ChangeDocumentAsync(file, "body { co\n");
        await Task.Delay(1000);
        var items = await client.CompletionAsync(file, new TextPosition(0, 10));
        Console.WriteLine($"{items.Count} suggestion(s) : {string.Join(", ", items.Take(8).Select(i => i.Label))}");

        await client.CloseDocumentAsync(file);
        await client.StopAsync();
        Console.WriteLine("Terminé.");
    }

    private static async Task<bool> TryStartAsync(LanguageClient client, string language, string root)
    {
        try
        {
            Console.WriteLine($"Démarrage du serveur {language} (Node peut mettre quelques secondes)...");
            await client.StartServerAsync(language, root);
            return true;
        }
        catch (AppException e)
        {
            Console.WriteLine($"ERREUR {e.Code} : {e.Message}");
            return false;
        }
    }
}