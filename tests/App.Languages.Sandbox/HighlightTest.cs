using App.Core.Models;
using App.Languages;

static class HighlightTest
{
    public static async Task RunAsync()
    {
        var editor = new FakeEditor();
        using var highlighter = new SyntaxHighlighter(editor);
        highlighter.TokensInvalidated += (_, e) =>
            Console.WriteLine($"  → à redessiner : lignes {e.FirstLine} à {e.LastLine}");

        Console.WriteLine("--- Ouverture d'un document (DocumentOpened) ---");
        var doc = editor.Create("def saluer(nom):\n    return \"salut\"\n", "python");
        Print(highlighter, doc.Id, 3);

        Console.WriteLine("--- Modification (DocumentChanged) : ouverture d'une chaîne ---");
        var origin = new TextPosition(0, 0);
        editor.ApplyEdit(doc.Id, new TextEdit(new TextRange(origin, origin), "x = \"\"\"\n"));
        Print(highlighter, doc.Id, 4);

        Console.WriteLine("--- Enregistrer sous notes.txt (le langage change) ---");
        await editor.SaveAsAsync(doc.Id, "notes.txt");
        Print(highlighter, doc.Id, 4);
    }

    private static void Print(SyntaxHighlighter h, DocumentId id, int lines)
    {
        for (var i = 0; i < lines; i++)
        {
            var tokens = h.GetTokens(id, i);
            Console.WriteLine($"ligne {i} : {(tokens.Count == 0 ? "(rien)" : string.Join(", ", tokens.Select(t => $"{t.StartColumn}-{t.EndColumn} {t.Kind}")))}");
        }
    }
}