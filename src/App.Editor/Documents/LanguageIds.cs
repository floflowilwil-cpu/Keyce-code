namespace App.Editor.Documents;

/// <summary>Détermine l'identifiant de langage (convention LSP) à partir de l'extension du fichier.</summary>
public static class LanguageIds
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".csx"] = "csharp", [".py"] = "python",
        [".js"] = "javascript", [".mjs"] = "javascript", [".jsx"] = "javascriptreact",
        [".ts"] = "typescript", [".tsx"] = "typescriptreact",
        [".json"] = "json", [".jsonc"] = "jsonc", [".md"] = "markdown",
        [".html"] = "html", [".htm"] = "html", [".css"] = "css", [".scss"] = "scss",
        [".xml"] = "xml", [".axaml"] = "xml", [".xaml"] = "xml", [".csproj"] = "xml", [".slnx"] = "xml",
        [".yml"] = "yaml", [".yaml"] = "yaml", [".sh"] = "shellscript", [".ps1"] = "powershell",
        [".sql"] = "sql", [".java"] = "java", [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".hpp"] = "cpp",
        [".go"] = "go", [".rs"] = "rust", [".php"] = "php", [".txt"] = "plaintext",
    };

    public const string PlainText = "plaintext";

    public static string FromPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return PlainText;
        return ByExtension.TryGetValue(Path.GetExtension(path), out var id) ? id : PlainText;
    }
}
