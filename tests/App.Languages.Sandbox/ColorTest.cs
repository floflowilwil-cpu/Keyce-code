using App.Languages;

static class ColorTest
{
    public static Task RunAsync()
    {
        var provider = new GrammarProvider();
        var samples = new Dictionary<string, string>
        {
            ["csharp"] = "using System;\nnamespace App;\npublic class Chat { public string Nom { get; set; } // note\n  void Parler() { Console.WriteLine(\"miaou\\n\"); } }\n",
            ["javascript"] = "const x = (a) => { return a + 1; }; // note\nconsole.log(\"salut\");\n",
            ["typescript"] = "interface Chat { nom: string }\nconst c: Chat = { nom: 'a' };\n",
            ["html"] = "<div class=\"a\">Bonjour</div>\n<!-- note -->\n",
            ["css"] = "body { color: red; margin: 0; }\n",
            ["json"] = "{ \"nom\": \"chat\", \"age\": 3, \"ok\": true }\n",
            ["markdown"] = "# Titre\n\nDu **gras** et un [lien](http://x.y) avec `code`.\n",
        };

        foreach (var (language, code) in samples)
        {
            var grammar = provider.Get(language);
            if (grammar is null) { Console.WriteLine($"{language} : AUCUNE grammaire"); continue; }

            var tokenizer = new DocumentTokenizer(grammar);
            tokenizer.SetText(code);

            var kinds = Enumerable.Range(0, tokenizer.LineCount)
                .SelectMany(i => tokenizer.GetTokens(i))
                .GroupBy(t => t.Kind)
                .Select(g => $"{g.Key} x{g.Count()}");

            Console.WriteLine($"{language} : {string.Join(", ", kinds)}");
        }
        return Task.CompletedTask;
    }
}