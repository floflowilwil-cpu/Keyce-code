using System.Text.Json.Nodes;
using App.Integrations.Execution;

namespace App.Integrations.Debug;

/// <summary>Mode de communication avec l'adaptateur de débogage (DAP).</summary>
public enum DebugTransport
{
    /// <summary>Messages DAP sur l'entrée / la sortie standard du processus.</summary>
    Stdio,

    /// <summary>Le processus écoute sur un port TCP local ; on s'y connecte.</summary>
    Tcp
}

/// <summary>Informations transmises à un adaptateur pour construire la requête DAP « launch ».</summary>
public sealed record DebugLaunchContext(
    string Program,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment);

/// <summary>Commande qui démarre un adaptateur de débogage.</summary>
public sealed record DebugAdapterCommand(
    string FileName,
    IReadOnlyList<string> Arguments);

/// <summary>Décrit comment déboguer un langage : quel adaptateur lancer et comment le configurer.</summary>
public sealed class DebugAdapterDefinition
{
    /// <summary>Identifiants de langage gérés (le premier est l'identifiant principal).</summary>
    public required IReadOnlyList<string> LanguageIds { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Nom de l'outil de débogage, pour les messages d'erreur.</summary>
    public required string DebuggerName { get; init; }

    /// <summary>Valeur du champ « adapterID » de la requête DAP « initialize ».</summary>
    public required string AdapterId { get; init; }

    /// <summary>Extensions de programme (avec le point) reconnues quand aucun langage n'est précisé.</summary>
    public required IReadOnlyList<string> ProgramExtensions { get; init; }

    public DebugTransport Transport { get; init; } = DebugTransport.Stdio;

    /// <summary>Délai maximal pour démarrer (compilation éventuelle comprise) et atteindre le premier arrêt.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Message affiché quand l'outil de débogage est introuvable.</summary>
    public required string MissingMessage { get; init; }

    /// <summary>Conseil d'installation, ajouté quand l'adaptateur s'arrête dès le démarrage.</summary>
    public required string InstallHint { get; init; }

    /// <summary>Construit la commande (le port est utilisé en mode TCP). Retourne null si l'outil est absent.</summary>
    public required Func<int, DebugAdapterCommand?> CreateCommand { get; init; }

    /// <summary>Construit les arguments de la requête DAP « launch ».</summary>
    public required Func<DebugLaunchContext, JsonObject> CreateLaunchArguments { get; init; }

    /// <summary>Vérifie le programme (lève une exception s'il ne convient pas). Optionnel.</summary>
    public Action<string>? ValidateProgram { get; init; }

    public string PrimaryLanguageId => LanguageIds[0];
}

/// <summary>
/// Liste des adaptateurs de débogage disponibles. Pour prendre en charge un nouveau langage,
/// il suffit d'ajouter une <see cref="DebugAdapterDefinition"/> à la liste.
/// </summary>
public sealed class DebugAdapterRegistry
{
    private static readonly string[] NativeSourceExtensions =
        [".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".rs"];

    private readonly List<DebugAdapterDefinition> _adapters;

    public DebugAdapterRegistry(IEnumerable<DebugAdapterDefinition> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToList();
    }

    public IReadOnlyList<DebugAdapterDefinition> Adapters => _adapters;

    public static DebugAdapterRegistry CreateDefault() => new(
    [
        CreateCSharp(),
        CreatePython(),
        CreateGo(),
        CreateNative()
    ]);

    /// <summary>
    /// Choisit l'adaptateur d'après l'identifiant de langage, ou à défaut d'après
    /// l'extension du programme (.dll, .py, .go, exécutable natif).
    /// </summary>
    public DebugAdapterDefinition Resolve(string? languageId, string program)
    {
        if (!string.IsNullOrWhiteSpace(languageId))
        {
            string wanted = languageId.Trim();

            return _adapters.FirstOrDefault(adapter =>
                       adapter.LanguageIds.Contains(wanted, StringComparer.OrdinalIgnoreCase))
                   ?? throw new NotSupportedException(
                       $"Le débogage du langage « {wanted} » n'est pas pris en charge. "
                       + $"Langages disponibles : {SupportedLanguages()}.");
        }

        string extension = Path.GetExtension(program);

        return _adapters.FirstOrDefault(adapter =>
                   adapter.ProgramExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
               ?? throw new NotSupportedException(
                   $"Impossible de déboguer « {Path.GetFileName(program)} » : type de programme non reconnu. "
                   + $"Langages disponibles : {SupportedLanguages()}.");
    }

    private string SupportedLanguages() =>
        string.Join(", ", _adapters
            .Select(adapter => adapter.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    // ---------------------------------------------------------------------
    //  Adaptateurs
    // ---------------------------------------------------------------------

    private static DebugAdapterDefinition CreateCSharp() => new()
    {
        LanguageIds = ["csharp", "cs", "dotnet"],
        DisplayName = "C# (.NET)",
        DebuggerName = "NetCoreDbg",
        AdapterId = "coreclr",
        ProgramExtensions = [".dll"],
        MissingMessage =
            "NetCoreDbg est introuvable. Vérifiez son installation ou ajoutez son dossier au PATH.",
        InstallHint = "Installez NetCoreDbg (par exemple : scoop install netcoredbg).",
        ValidateProgram = program =>
        {
            if (!program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Le programme doit être un fichier .NET .dll.");
        },
        CreateCommand = _ =>
        {
            string scoopPath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "scoop",
                "shims",
                "netcoredbg.exe");

            string? found = File.Exists(scoopPath)
                ? scoopPath
                : Locate("csharp", "netcoredbg");

            return found is null
                ? null
                : new DebugAdapterCommand(found, ["--interpreter=vscode"]);
        },
        CreateLaunchArguments = context =>
        {
            var launch = new JsonObject
            {
                ["program"] = context.Program,
                ["args"] = ToJsonArray(context.Arguments),
                ["cwd"] = context.WorkingDirectory,
                ["stopAtEntry"] = true,
                ["justMyCode"] = true
            };

            if (context.Environment is not null)
                launch["env"] = ToJsonObject(context.Environment);

            return launch;
        }
    };

    private static DebugAdapterDefinition CreatePython() => new()
    {
        LanguageIds = ["python", "py"],
        DisplayName = "Python",
        DebuggerName = "debugpy",
        AdapterId = "python",
        ProgramExtensions = [".py", ".pyw"],
        MissingMessage =
            "Python est introuvable dans le PATH. Installez Python puis le module debugpy "
            + "(python -m pip install debugpy).",
        InstallHint = "Installez debugpy : python -m pip install debugpy",
        ValidateProgram = program =>
        {
            string extension = Path.GetExtension(program);

            if (!extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".pyw", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Le programme doit être un fichier Python (.py).");
            }
        },
        CreateCommand = _ =>
        {
            string? python = Locate(
                "python",
                OperatingSystem.IsWindows()
                    ? new[] { "python", "py" }
                    : new[] { "python3", "python" });

            return python is null
                ? null
                : new DebugAdapterCommand(python, ["-m", "debugpy.adapter"]);
        },
        CreateLaunchArguments = context =>
        {
            var launch = new JsonObject
            {
                ["type"] = "python",
                ["request"] = "launch",
                ["name"] = "Keyce Code",
                ["program"] = context.Program,
                ["args"] = ToJsonArray(context.Arguments),
                ["cwd"] = context.WorkingDirectory,
                ["console"] = "internalConsole",
                ["stopOnEntry"] = true,
                ["justMyCode"] = true
            };

            if (context.Environment is not null)
                launch["env"] = ToJsonObject(context.Environment);

            return launch;
        }
    };

    private static DebugAdapterDefinition CreateGo() => new()
    {
        LanguageIds = ["go", "golang"],
        DisplayName = "Go",
        DebuggerName = "Delve",
        AdapterId = "go",
        ProgramExtensions = [".go"],
        Transport = DebugTransport.Tcp,
        // Delve compile le programme au démarrage : la première compilation peut être longue.
        StartupTimeout = TimeSpan.FromSeconds(120),
        MissingMessage =
            "Delve (dlv) est introuvable dans le PATH. Installez-le : "
            + "go install github.com/go-delve/delve/cmd/dlv@latest",
        InstallHint = "Installez Delve : go install github.com/go-delve/delve/cmd/dlv@latest",
        ValidateProgram = program =>
        {
            if (!Path.GetExtension(program).Equals(".go", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Le programme doit être un fichier Go (.go).");
        },
        CreateCommand = port =>
        {
            string? dlv = Locate("go", "dlv");

            return dlv is null
                ? null
                : new DebugAdapterCommand(dlv, ["dap", $"--listen=127.0.0.1:{port}"]);
        },
        CreateLaunchArguments = context =>
        {
            var launch = new JsonObject
            {
                ["request"] = "launch",
                ["mode"] = "debug",
                ["program"] = context.Program,
                ["args"] = ToJsonArray(context.Arguments),
                ["cwd"] = context.WorkingDirectory,
                ["stopOnEntry"] = true
            };

            if (context.Environment is not null)
                launch["env"] = ToJsonObject(context.Environment);

            return launch;
        }
    };

    /// <summary>C, C++ et Rust : exécutable natif compilé avec les symboles de débogage, via lldb-dap.</summary>
    private static DebugAdapterDefinition CreateNative() => new()
    {
        LanguageIds = ["c", "cpp", "c++", "rust"],
        DisplayName = "C / C++ / Rust",
        DebuggerName = "lldb-dap",
        AdapterId = "lldb-dap",
        ProgramExtensions = ["", ".exe", ".out", ".elf"],
        MissingMessage =
            "lldb-dap est introuvable dans le PATH. Installez LLVM (il fournit lldb-dap) "
            + "puis relancez l'application.",
        InstallHint = "Installez LLVM / LLDB (lldb-dap) et vérifiez qu'il est dans le PATH.",
        ValidateProgram = program =>
        {
            string extension = Path.GetExtension(program);

            if (NativeSourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Le débogage natif nécessite l'exécutable compilé, pas le fichier source.");
            }
        },
        CreateCommand = _ =>
        {
            string? lldb = Locate("native", "lldb-dap", "lldb-vscode");

            return lldb is null
                ? null
                : new DebugAdapterCommand(lldb, []);
        },
        CreateLaunchArguments = context =>
        {
            var launch = new JsonObject
            {
                ["program"] = context.Program,
                ["args"] = ToJsonArray(context.Arguments),
                ["cwd"] = context.WorkingDirectory,
                ["stopOnEntry"] = true
            };

            if (context.Environment is not null)
            {
                // lldb-dap attend une liste de chaînes « NOM=valeur ».
                launch["env"] = new JsonArray(
                    context.Environment
                        .Select(variable =>
                            (JsonNode?)JsonValue.Create($"{variable.Key}={variable.Value}"))
                        .ToArray());
            }

            return launch;
        }
    };

    // ---------------------------------------------------------------------
    //  Outils
    // ---------------------------------------------------------------------

    /// <summary>
    /// Cherche l'outil dans le PATH. La variable d'environnement KEYCE_DEBUGGER_&lt;LANGAGE&gt;
    /// (par exemple KEYCE_DEBUGGER_PYTHON) permet d'indiquer un chemin précis.
    /// </summary>
    private static string? Locate(string languageKey, params string[] tools)
    {
        string? overridePath = System.Environment.GetEnvironmentVariable(
            "KEYCE_DEBUGGER_" + languageKey.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (string tool in tools)
        {
            string? found = ToolLocator.FindOnPath(tool);

            if (found is not null)
                return found;
        }

        return null;
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> values)
    {
        var result = new JsonObject();

        foreach (var variable in values)
            result[variable.Key] = variable.Value;

        return result;
    }
}
