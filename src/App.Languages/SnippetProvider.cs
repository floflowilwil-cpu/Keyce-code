using App.Core.Contracts;
using App.Core.Models;

namespace App.Languages;

public sealed class SnippetProvider : ISnippetProvider
{
    private sealed record Snippet(string Prefix, string Description, string Body);

    private static readonly Dictionary<string, Snippet[]> ByLanguage =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["python"] =
            [
                new("def", "Définir une fonction", "def nom(parametres):\n    pass\n"),
                new("class", "Définir une classe", "class Nom:\n    def __init__(self):\n        pass\n"),
                new("for", "Boucle for", "for element in iterable:\n    pass\n"),
                new("while", "Boucle while", "while condition:\n    pass\n"),
                new("if", "Condition if", "if condition:\n    pass\n"),
                new("ifelse", "Condition if / else", "if condition:\n    pass\nelse:\n    pass\n"),
                new("try", "Bloc try / except", "try:\n    pass\nexcept Exception as e:\n    print(e)\n"),
                new("with", "Bloc with", "with open('fichier.txt') as f:\n    pass\n"),
                new("main", "Point d'entrée", "if __name__ == \"__main__\":\n    main()\n"),
            ],
            ["csharp"] =
            [
                new("class", "Définir une classe", "public class Nom\n{\n    \n}\n"),
                new("prop", "Propriété automatique", "public string Nom { get; set; }"),
                new("for", "Boucle for", "for (var i = 0; i < n; i++)\n{\n    \n}\n"),
                new("foreach", "Boucle foreach", "foreach (var element in collection)\n{\n    \n}\n"),
                new("if", "Condition if", "if (condition)\n{\n    \n}\n"),
                new("try", "Bloc try / catch", "try\n{\n    \n}\ncatch (Exception e)\n{\n    \n}\n"),
                new("cw", "Console.WriteLine", "Console.WriteLine();"),
            ],
            ["javascript"] =
            [
                new("function", "Définir une fonction", "function nom(parametres) {\n    \n}\n"),
                new("arrow", "Fonction fléchée", "const nom = (parametres) => {\n    \n};\n"),
                new("for", "Boucle for", "for (let i = 0; i < n; i++) {\n    \n}\n"),
                new("forof", "Boucle for...of", "for (const element of iterable) {\n    \n}\n"),
                new("if", "Condition if", "if (condition) {\n    \n}\n"),
                new("try", "Bloc try / catch", "try {\n    \n} catch (e) {\n    console.error(e);\n}\n"),
                new("log", "console.log", "console.log();"),
            ],
        };

    public IReadOnlyList<CompletionItem> GetSnippets(string languageId, string prefix)
    {
        if (!ByLanguage.TryGetValue(languageId, out var snippets)) return [];

        return snippets
            .Where(s => s.Prefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(s => new CompletionItem(s.Prefix, s.Body, "Snippet", s.Description))
            .ToList();
    }
}