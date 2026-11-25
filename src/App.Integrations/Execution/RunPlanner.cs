using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
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

            ".csproj" => PlanCSharpProject(file),

            ".html" or ".htm" => PlanHtmlPreview(file, directory),

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

    public DebugPlan PlanDebug(string filePath)
    {
        string file = RequireFile(filePath);
        string directory = Path.GetDirectoryName(file)!;
        string extension = Path.GetExtension(file).ToLowerInvariant();

        switch (extension)
        {
            case ".cs":
            case ".csproj":
                // Un projet temporaire est créé pour les fichiers isolés ; le fichier source reste à son emplacement.
                return new DebugPlan("csharp", "C# (.NET)", PlanDebugBuild(file), null);

            case ".html":
            case ".htm":
                throw new NotSupportedException(
                    "HTML est prévisualisé dans le navigateur et ne se compile pas. "
                    + "Le débogage des scripts JavaScript intégrés à la page nécessite un adaptateur de navigateur, "
                    + "qui n'est pas encore configuré dans Keyce Code.");

            case ".js":
            case ".mjs":
            case ".cjs":
                throw new NotSupportedException(
                    "JavaScript peut être exécuté avec Node.js. Son débogage pas à pas nécessite "
                    + "l'adaptateur JavaScript Debug (js-debug), qui n'est pas encore configuré.");

            case ".py":
                Require("Python", OperatingSystem.IsWindows()
                    ? new[] { "python", "py" }
                    : new[] { "python3", "python" });

                return new DebugPlan("python", "Python", null, file);

            case ".go":
                Require("Go", "go");

                return new DebugPlan("go", "Go", null, file);

            case ".c":
                return PlanDebugCompiled(
                    "c", "C", file, directory,
                    Require("Un compilateur C (gcc ou clang)", "gcc", "clang", "cc"));

            case ".cpp":
            case ".cc":
            case ".cxx":
                return PlanDebugCompiled(
                    "cpp", "C++", file, directory,
                    Require("Un compilateur C++ (g++ ou clang++)", "g++", "clang++", "c++"));

            case ".rs":
                return PlanDebugRust(file, directory);

            default:
                throw new NotSupportedException(
                    extension.Length == 0
                        ? "Impossible de détecter le langage : le fichier n'a pas d'extension."
                        : $"Le débogage des fichiers « {extension} » n'est pas encore configuré dans Keyce Code.");
        }
    }

    public RunPlan PlanDebugBuild(string filePath)
    {
        string file = RequireFile(filePath);

        string extension = Path.GetExtension(file);
        bool isProjectFile = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
        if (!isProjectFile && !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "La compilation .NET concerne les fichiers C# (.cs) ou les projets (.csproj).");
        }

        string? existingProject = isProjectFile ? file : FindProjectFile(Path.GetDirectoryName(file)!);
        string project = existingProject ?? CreateStandaloneCSharpProject(file);
        string workingDirectory = existingProject is not null
            ? Path.GetDirectoryName(project)!
            : Path.GetDirectoryName(file)!;

        string dotnet = Require(".NET SDK", "dotnet");

        var step = new RunStep(
            "Compilation de " + Path.GetFileName(project),
            new ExecutionStartOptions(
                dotnet,
                new[] { "build", project, "-c", "Debug", "-nologo" },
                workingDirectory));

        return new RunPlan("csharp", "C# (.NET)", new[] { step });
    }

    public string FindDebugAssembly(string filePath)
    {
        string file = RequireFile(filePath);

        string project = Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            ? file
            : FindProjectFile(Path.GetDirectoryName(file)!) ?? CreateStandaloneCSharpProject(file);

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

        // Fichier isolé : générer un petit projet temporaire pour rester compatible avec les SDK .NET 8+.
        string standaloneProject = CreateStandaloneCSharpProject(file);
        return Single(
            "csharp", "C# (fichier isolé)", file, dotnet,
            new[] { "run", "--project", standaloneProject },
            directory);
    }

    private RunPlan PlanCSharpProject(string projectFile)
    {
        string dotnet = Require(".NET SDK", "dotnet");
        string projectDirectory = Path.GetDirectoryName(projectFile)!;
        return Single(
            "csharp", "C# (.NET)", projectFile, dotnet,
            new[] { "run", "--project", projectFile },
            projectDirectory);
    }

    private RunPlan PlanHtmlPreview(string file, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            string shell = Require("Invite de commandes Windows", "cmd.exe", "cmd");
            return Single("html", "HTML (navigateur)", file, shell, new[] { "/c", "start", "", file }, directory);
        }

        if (OperatingSystem.IsMacOS())
        {
            string opener = Require("Navigateur par défaut", "open");
            return Single("html", "HTML (navigateur)", file, opener, new[] { file }, directory);
        }

        string linuxOpener = Require("Navigateur par défaut", "xdg-open");
        return Single("html", "HTML (navigateur)", file, linuxOpener, new[] { file }, directory);
    }

    /// <summary>
    /// Crée un projet .NET temporaire déterministe pour compiler un .cs isolé sans modifier son dossier.
    /// Le projet inclut le fichier source original par chemin absolu.
    /// </summary>
    private static string CreateStandaloneCSharpProject(string sourceFile)
    {
        string normalized = Path.GetFullPath(sourceFile);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        string safeName = Path.GetFileNameWithoutExtension(normalized);
        foreach (char invalid in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Program";

        string projectDirectory = Path.Combine(Path.GetTempPath(), "KeyceCode", "standalone", hash);
        Directory.CreateDirectory(projectDirectory);
        string projectPath = Path.Combine(projectDirectory, safeName + ".csproj");

        var project = new XDocument(
            new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("OutputType", "Exe"),
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("ImplicitUsings", "enable"),
                    new XElement("Nullable", "enable"),
                    new XElement("DebugType", "portable"),
                    new XElement("DebugSymbols", "true"),
                    new XElement("AssemblyName", safeName)),
                new XElement("ItemGroup",
                    new XElement("Compile", new XAttribute("Include", normalized)))));

        project.Save(projectPath);
        return projectPath;
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

    /// <summary>Compile avec les symboles de débogage (-g) et sans optimisation (-O0), sans lancer le programme.</summary>
    private static DebugPlan PlanDebugCompiled(
        string languageId,
        string displayName,
        string file,
        string directory,
        string compiler)
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), "KeyceCode", "debug");
        Directory.CreateDirectory(outputDirectory);

        string output = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(file) + (OperatingSystem.IsWindows() ? ".exe" : ""));

        var build = new RunPlan(
            languageId,
            displayName,
            new[]
            {
                new RunStep(
                    "Compilation de " + Path.GetFileName(file) + " (débogage)",
                    new ExecutionStartOptions(
                        compiler,
                        new[] { file, "-g", "-O0", "-o", output },
                        directory))
            });

        return new DebugPlan(languageId, displayName, build, output);
    }

    private DebugPlan PlanDebugRust(string file, string directory)
    {
        string? cargoRoot = FindUpwards(directory, "Cargo.toml");

        if (cargoRoot is null)
        {
            string outputDirectory = Path.Combine(Path.GetTempPath(), "KeyceCode", "debug");
            Directory.CreateDirectory(outputDirectory);

            string output = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(file) + (OperatingSystem.IsWindows() ? ".exe" : ""));

            var single = new RunPlan(
                "rust",
                "Rust",
                new[]
                {
                    new RunStep(
                        "Compilation de " + Path.GetFileName(file) + " (débogage)",
                        new ExecutionStartOptions(
                            Require("Rust (rustc)", "rustc"),
                            new[] { file, "-g", "-C", "opt-level=0", "-o", output },
                            directory))
                });

            return new DebugPlan("rust", "Rust", single, output);
        }

        string packageName = ReadCargoPackageName(Path.Combine(cargoRoot, "Cargo.toml"))
            ?? throw new InvalidOperationException(
                "Impossible de lire le nom du paquet dans Cargo.toml.");

        string binary = Path.Combine(
            cargoRoot,
            "target",
            "debug",
            packageName + (OperatingSystem.IsWindows() ? ".exe" : ""));

        var cargo = new RunPlan(
            "rust",
            "Rust (cargo)",
            new[]
            {
                new RunStep(
                    "Compilation du projet Cargo (débogage)",
                    new ExecutionStartOptions(
                        Require("Cargo", "cargo"),
                        new[] { "build" },
                        cargoRoot))
            });

        return new DebugPlan("rust", "Rust (cargo)", cargo, binary);
    }

    /// <summary>Lit « name = "..." » dans la section [package] de Cargo.toml.</summary>
    private static string? ReadCargoPackageName(string cargoToml)
    {
        try
        {
            bool inPackage = false;

            foreach (string rawLine in File.ReadLines(cargoToml))
            {
                string line = rawLine.Trim();

                if (line.StartsWith('['))
                {
                    inPackage = line == "[package]";
                    continue;
                }

                if (!inPackage || !line.StartsWith("name", StringComparison.Ordinal))
                    continue;

                int equals = line.IndexOf('=');

                if (equals < 0)
                    continue;

                string value = line[(equals + 1)..].Trim().Trim('"', '\'');

                if (value.Length > 0)
                    return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fichier illisible : l'appelant signale l'erreur.
        }

        return null;
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
