using App.Core.Models;
using App.Languages;

static class SyncTest
{
    public static async Task RunAsync()
    {
        var editor = new FakeEditor();
        using var client = new LanguageClient();
        using var sync = new LanguageDocumentSync(editor, client);

        sync.DiagnosticsPublished += (_, e) =>
        {
            Console.WriteLine($"  Diagnostics ({e.Diagnostics.Count}) pour le document {e.Id.Value.ToString()[..8]}");
            foreach (var d in e.Diagnostics)
                Console.WriteLine($"    ligne {d.Range.Start.Line + 1} : {d.Message}");
        };
        sync.ErrorOccurred += (_, e) => Console.WriteLine($"  ERREUR {e.Code} : {e.Message}");

        Console.WriteLine("--- Document sans titre en Python, avec deux erreurs ---");
        var doc = editor.Create("import os\nprint(valeur_inconnue)\n", "python");
        await Task.Delay(5000);

        Console.WriteLine("--- Correction (modifications incrémentales) ---");
        editor.ApplyEdit(doc.Id, new TextEdit(new TextRange(new TextPosition(0, 0), new TextPosition(1, 0)), ""));
        var origin = new TextPosition(0, 0);
        editor.ApplyEdit(doc.Id, new TextEdit(new TextRange(origin, origin), "valeur_inconnue = 1\n"));
        await Task.Delay(3000);

        Console.WriteLine("--- Enregistrer sous sync_test.py ---");
        await editor.SaveAsAsync(doc.Id, Path.Combine(Directory.GetCurrentDirectory(), "sync_test.py"));
        await Task.Delay(3000);

        Console.WriteLine("--- Fermeture ---");
        editor.CloseDocument(doc.Id);
        await Task.Delay(500);

        await client.StopAsync();
        Console.WriteLine("Terminé.");
    }
}