using System.Xml;
using System.Xml.Linq;
using App.Core.Contracts;
using App.Core.Models;

namespace App.Integrations.Execution;

/// <summary>
/// Détecte automatiquement le langage d'un fichier (d'après son extension) et construit
/// les commandes à lancer : interpréteur, compilateur puis exécutable, ou projet .NET.
/// </summary>
public sealed class RunPlanner : IRunPlanner
{
    private static readonly IReadOnlyDictionary<string, string> PythonEnvironment =
        new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" };

    private readonly Func<string, string?> _locate;

    public RunPlanner() : this(ToolLocator.FindOnPath)
    {
    }

    /// <param name="locate">Retourne le chemin complet d'un outil du PATH, ou null s'il est absent.</param>
    public RunPlanner(Func<string, string?> locate)
    {
        ArgumentNullException.ThrowIfNull(locate);
        _locate = locate;
    }

    public RunPlan PlanRun(string filePath)
    {
        string file = RequireFile(filePath);
        string directory = Path.GetDirectoryName(file)!;
        string extension = Path.GetExtension(file).ToLowerInvariant();

        return extension switch
        {
            ".cs" => PlanCSharp(file, directory),

            ".py" => Single(
                "python", "Python", file,
                Require("Python", OperatingSystem.IsWindows()
                    ? new[] { "python", "py" }
                    : new[] { "python3", "python" }),
                new[] { "-u", file },
                directory,
                PythonEnvironment),

            ".js" or ".mjs" or ".cjs" => Single(
                "javascript", "JavaScript (Node.js)", file,
                Require("Node.js", "node"),
                new[] { file },
                directory),

            ".ts" or ".mts" or ".cts" => Single(
                "typescript", "TypeScript (tsx)", file,
                Require("Node.js (npx)", "npx"),
                new[] { "--yes", "tsx", file },
                directory),

            ".java" => Single(
                "java", "Java", file,
                Require("Java (JDK 11 ou plus)", "java"),
                new[] { file },
                directory),

            ".go" => Single(
                "go", "Go", file,
                Require("Go", "go"),
                new[] { "run", file },
                directory),

            ".php" => Single(
                "php", "PHP", file,
                Require("PHP", "php"),
                new[] { file },
                directory),

            ".rb" => Single(
                "ruby", "Ruby", file,
                Require("Ruby", "ruby"),
                new[] { file },
                directory),

            ".ps1" => Single(
                "powershell", "PowerShell", file,
                Require("PowerShell", "pwsh", "powershell"),
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file },
                directory),

            ".sh" => Single(
                "shellscript", "Shell (bash)", file,
                Require("Bash", "bash", "sh"),
                new[] { file },
                directory),

            ".bat" or ".cmd" => PlanBatch(file, directory),

            ".c" => PlanCompiled(
                "c", "C", file, directory,
                Require("Un compilateur C (gcc ou clang)", "gcc", "clang", "cc")),

            ".cpp" or ".cc" or ".cxx" => PlanCompiled(
                "cpp", "C++", file, directory,
                Require("Un compilateur C++ (g++ ou clang++)", "g++", "clang++", "c++")),

            ".rs" => PlanRust(file, directory),

            _ => throw new NotSupportedException(
                extension.Length == 0
                    ? "Impossible de détecter le langage : le fichier n'a pas d'extension."
                    : $"Les fichiers « {extension} » ne sont pas exécutables (aucun interpréteur connu).")
        };
    }

    public RunPlan PlanDebugBuild(string filePath)
    {
        string file = RequireFile(filePath);

        if (!Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Le débogage prend en charge uniquement C# (.NET) pour le moment.");
        }

        string project = FindProjectFile(Path.GetDirectoryName(file)!)
            ?? throw new InvalidOperationException(
                "Aucun fichier .csproj trouvé : le débogage nécessite un projet .NET.");

        string dotnet = Require(".NET SDK", "dotnet");

        var step = new RunStep(
            "Compilation de " + Path.GetFileName(project),
            new ExecutionStartOptions(
                dotnet,
                new[] { "build", project, "-c", "Debug", "-nologo" },
                Path.GetDirectoryName(project)));

        return new RunPlan("csharp", "C# (.NET)", new[] { step });
    }

    public string FindDebugAssembly(string filePath)
    {
        string file = RequireFile(filePath);

        string project = FindProjectFile(Path.GetDirectoryName(file)!)
            ?? throw new InvalidOperationException(
                "Aucun fichier .csproj trouvé : le débogage nécessite un projet .NET.");

        string assemblyName = ReadAssemblyName(project)
            ?? Path.GetFileNameWithoutExtension(project);

        string debugRoot = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug");

        string? best = Directory.Exists(debugRoot)
            ? Directory
                .GetFiles(debugRoot, assemblyName + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault()
            : null;

        return best ?? throw new FileNotFoundException(
            $"Le programme compilé « {assemblyName}.dll » est introuvable dans {debugRoot}.",
            Path.Combine(debugRoot, assemblyName + ".dll"));
    }

    // ---------------------------------------------------------------------
    //  Plans par langage
    // ---------------------------------------------------------------------

    private RunPlan PlanCSharp(string file, string directory)
    {
        string dotnet = Require(".NET SDK", "dotnet");
        string? project = FindProjectFile(directory);

        if (project is not null)
        {
            return Single(
                "csharp", "C# (.NET)", file, dotnet,
                new[] { "run", "--project", project },
                Path.GetDirectoryName(project)!);
        }

        // Fichier isolé sans projet : « dotnet run fichier.cs » (SDK .NET 10 ou plus).
        return Single(
            "csharp", "C# (fichier isolé)", file, dotnet,
            new[] { "run", file },
            directory);
    }

    private RunPlan PlanRust(string file, string directory)
    {
        string? cargoRoot = FindUpwards(directory, "Cargo.toml");

        if (cargoRoot is not null)
        {
            return Single(
                "rust", "Rust (cargo)", file,
                Require("Cargo", "cargo"),
                new[] { "run" },
                cargoRoot);
        }

        return PlanCompiled("rust", "Rust", file, directory, Require("Rust (rustc)", "rustc"));
    }

    private static RunPlan PlanBatch(string file, string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new NotSupportedException(
                "Les scripts .bat et .cmd ne peuvent s'exécuter que sous Windows.");
        }

        string shell = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec
            ? comSpec
            : "cmd.exe";

        return Single("batch", "Batch (cmd)", file, shell, new[] { "/c", file }, directory);
    }

    private static RunPlan PlanCompiled(
        string languageId,
        string displayName,
        string file,
        string directory,
        string compiler)
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), "KeyceCode", "run");
        Directory.CreateDirectory(outputDirectory);

        string output = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(file) + (OperatingSystem.IsWindows() ? ".exe" : ""));

        var steps = new[]
        {
            new RunStep(
                "Compilation de " + Path.GetFileName(file),
                new ExecutionStartOptions(compiler, new[] { file, "-o", output }, directory)),
            new RunStep(
                "Exécution de " + Path.GetFileName(file),
                new ExecutionStartOptions(output, null, directory))
        };

        return new RunPlan(languageId, displayName, steps);
    }

    private static RunPlan Single(
        string languageId,
        string displayName,
        string file,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var step = new RunStep(
            "Exécution de " + Path.GetFileName(file),
            new ExecutionStartOptions(executable, arguments, workingDirectory, environment));

        return new RunPlan(languageId, displayName, new[] { step });
    }

    // ---------------------------------------------------------------------
    //  Outils
    // ---------------------------------------------------------------------

    private string Require(string displayName, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            string? found = _locate(candidate);

            if (found is not null)
                return found;
        }

        throw new InvalidOperationException(
            $"{displayName} est introuvable dans le PATH (recherché : {string.Join(", ", candidates)}). "
            + "Installez-le puis relancez l'application.");
    }

    private static string RequireFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Le chemin du fichier est obligatoire.", nameof(filePath));

        string full = Path.GetFullPath(filePath);

        if (!File.Exists(full))
            throw new FileNotFoundException("Fichier introuvable : " + full, full);

        return full;
    }

    /// <summary>Premier .csproj trouvé en remontant depuis le dossier du fichier.</summary>
    private static string? FindProjectFile(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            string[] projects = Directory.GetFiles(current.FullName, "*.csproj");

            if (projects.Length > 0)
            {
                Array.Sort(projects, StringComparer.OrdinalIgnoreCase);
                return projects[0];
            }
        }

        return null;
    }

    /// <summary>Dossier le plus proche (en remontant) qui contient le fichier indiqué.</summary>
    private static string? FindUpwards(string directory, string fileName)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, fileName)))
                return current.FullName;
        }

        return null;
    }

    private static string? ReadAssemblyName(string projectPath)
    {
        try
        {
            string? name = XDocument.Load(projectPath)
                .Descendants("AssemblyName")
                .Select(element => element.Value.Trim())
                .FirstOrDefault(value => value.Length > 0 && !value.Contains("$("));

            return name;
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Recherche d'un outil dans le PATH (extensions PATHEXT prises en compte sous Windows).</summary>
internal static class ToolLocator
{
    public static string? FindOnPath(string tool)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(path))
            return null;

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "" };

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate;

                try
                {
                    candidate = Path.Combine(directory.Trim().Trim('"'), tool + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
