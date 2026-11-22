using App.Languages;

static class SnippetTest
{
    public static Task RunAsync()
    {
        var provider = new SnippetProvider();

        foreach (var prefix in new[] { "f", "if", "xyz" })
        {
            var results = provider.GetSnippets("python", prefix);
            Console.WriteLine($"Python, préfixe '{prefix}' : {results.Count} snippet(s)");
            foreach (var s in results)
                Console.WriteLine($"  {s.Label} ({s.Kind}) - {s.Detail}");
        }

        Console.WriteLine("--- Corps du snippet 'ifelse' ---");
        Console.WriteLine(provider.GetSnippets("python", "ifelse")[0].InsertText);

        Console.WriteLine($"Langage inconnu : {provider.GetSnippets("cobol", "if").Count} snippet(s)");
        return Task.CompletedTask;
    }
}
