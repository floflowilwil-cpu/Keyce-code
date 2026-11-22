using App.Core.Models;
using App.Languages;

static class LspExtrasTest
{
    public static async Task RunAsync()
    {
        using var client = new LanguageClient();
        var root = Directory.GetCurrentDirectory();
        await client.StartServerAsync("python", root);

        // Le renommage (rope) a besoin d'un vrai fichier sur le disque.
        var file = Path.Combine(root, "extras.py");
        const string code = "def saluer(nom):\n    return nom\n\nx = saluer('a')\ny = saluer('b')\n";
        File.WriteAllText(file, code);

        try
        {
            await client.OpenDocumentAsync(file, "python", code);
            await Task.Delay(1500);   // laisse le serveur analyser

            Console.WriteLine("--- Références de 'saluer' ---");
            foreach (var r in await client.ReferencesAsync(file, new TextPosition(0, 5)))
                Console.WriteLine($"  ligne {r.Range.Start.Line + 1}, colonne {r.Range.Start.Column}");

            Console.WriteLine("--- Renommage en 'dire_bonjour' ---");
            var rename = await client.RenameAsync(file, new TextPosition(0, 5), "dire_bonjour");
            if (rename.Count == 0) Console.WriteLine("  (aucune modification : rope est-il installé ?)");
            foreach (var (path, edits) in rename)
                foreach (var e in edits)
                    Console.WriteLine($"  {Path.GetFileName(path)} ligne {e.Range.Start.Line + 1} → '{e.NewText}'");

            Console.WriteLine("--- Formatage de 'def f( a,b ):' ---");
            await client.ChangeDocumentAsync(file, "def f( a,b ):\n  return a+b\n");
            await Task.Delay(500);
            var format = await client.FormatDocumentAsync(file);
            Console.WriteLine($"  {format.Count} modification(s)");
            foreach (var e in format.Take(5))
                Console.WriteLine($"  ligne {e.Range.Start.Line + 1} → {e.NewText.Replace("\n", "\\n")}");

            await client.CloseDocumentAsync(file);
        }
        finally
        {
            await client.StopAsync();
            File.Delete(file);
        }
    }
}