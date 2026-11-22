using App.Core.Models;
using App.Languages;

static class SharedServerTest
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

        foreach (var language in new[] { "javascript", "typescriptreact", "typescript", "jsonc", "json", "scss", "css" })
            await client.StartServerAsync(language, root);

        Console.WriteLine($"Serveurs actifs : {string.Join(", ", client.RunningServers.Order())}");
        Console.WriteLine("Attendu : css, json, typescript (3 serveurs, pas 7)");

        Console.WriteLine("--- Un document .tsx (identifiant typescriptreact) ---");
        var file = Path.Combine(root, "test.tsx");
        await client.OpenDocumentAsync(file, "typescriptreact", "const n: number = \"texte\";\n");
        for (var i = 0; i < 2; i++)
            if (!await signal.WaitAsync(TimeSpan.FromSeconds(20))) break;

        await client.CloseDocumentAsync(file);
        await client.StopAsync();
        Console.WriteLine("Terminé.");
    }
}