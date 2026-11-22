using System.Text.Json;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Integrations.Tasks;

/// <summary>
/// Tâches d'un espace de travail. Format <c>.keyce/tasks.json</c> (compatible avec l'essentiel de <c>.vscode/tasks.json</c>) :
/// <code>
/// { "version": "1.0", "tasks": [ { "label": "Build", "command": "dotnet build", "args": [], "cwd": "${workspaceFolder}", "env": { } } ] }
/// </code>
/// </summary>
public sealed class TaskService : ITaskService
{
    public const string ConfigDirectoryName = ".keyce";
    public const string ConfigFileName = "tasks.json";

    private const string KeyceSource = ".keyce/tasks.json";
    private const string VsCodeSource = ".vscode/tasks.json";
    private const string DetectedSource = "détectée";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private const string DefaultConfig = """
        {
          "version": "1.0",
          "tasks": [
            {
              "label": "Dire bonjour",
              "command": "echo Bonjour depuis Keyce Code",
              "cwd": "${workspaceFolder}"
            }
          ]
        }
        """;

    public string GetConfigFilePath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ConfigDirectoryName, ConfigFileName);

    public string EnsureConfigFile(string workspaceRoot)
    {
        string path = GetConfigFilePath(workspaceRoot);

        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DefaultConfig + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppException(
                ErrorCode.AccessDenied,
                "Impossible de créer " + path + " : " + ex.Message,
                ex);
        }

        return path;
    }

    public IReadOnlyList<TaskDefinition> GetTasks(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return Array.Empty<TaskDefinition>();

        var result = new List<TaskDefinition>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRange(IEnumerable<TaskDefinition> tasks)
        {
            foreach (TaskDefinition task in tasks)
            {
                if (labels.Add(task.Label))
                    result.Add(task);
            }
        }

        AddRange(ReadConfig(GetConfigFilePath(workspaceRoot), workspaceRoot, KeyceSource));
        AddRange(ReadConfig(
            Path.Combine(workspaceRoot, ".vscode", "tasks.json"), workspaceRoot, VsCodeSource));
        AddRange(Detect(workspaceRoot));

        return result;
    }

    public RunPlan CreatePlan(TaskDefinition task, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (string.IsNullOrWhiteSpace(task.CommandLine))
        {
            throw new AppException(
                ErrorCode.InvalidArgument,
                $"La tâche « {task.Label} » n'a pas de commande.");
        }

        string workingDirectory = string.IsNullOrWhiteSpace(task.WorkingDirectory)
            ? workspaceRoot
            : Path.GetFullPath(task.WorkingDirectory, workspaceRoot);

        if (!Directory.Exists(workingDirectory))
        {
            throw new AppException(
                ErrorCode.NotFound,
                $"Le dossier d'exécution de la tâche « {task.Label} » est introuvable : {workingDirectory}");
        }

        ExecutionStartOptions options = OperatingSystem.IsWindows()
            // RawArguments évite que .NET ré-échappe les guillemets de la commande pour cmd.exe.
            ? new ExecutionStartOptions(
                "cmd.exe",
                WorkingDirectory: workingDirectory,
                Environment: task.Environment,
                RawArguments: "/d /s /c \"" + task.CommandLine + "\"")
            : new ExecutionStartOptions(
                "/bin/sh",
                new[] { "-c", task.CommandLine },
                workingDirectory,
                task.Environment);

        return new RunPlan(
            "task",
            "Tâche : " + task.Label,
            new[] { new RunStep(task.Label, options) });
    }

    // ---------- Fichiers de configuration ----------

    private static IReadOnlyList<TaskDefinition> ReadConfig(string path, string root, string source)
    {
        if (!File.Exists(path))
            return Array.Empty<TaskDefinition>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tasks", out JsonElement tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<TaskDefinition>();
            }

            var result = new List<TaskDefinition>();

            foreach (JsonElement item in tasks.EnumerateArray())
            {
                TaskDefinition? task = ParseTask(item, root, source);
                if (task is not null)
                    result.Add(task);
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new AppException(
                ErrorCode.InvalidArgument,
                $"Fichier de tâches invalide ({source}) : {ex.Message}",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppException(
                ErrorCode.AccessDenied,
                $"Lecture impossible de {source} : {ex.Message}",
                ex);
        }
    }

    private static TaskDefinition? ParseTask(JsonElement item, string root, string source)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        string? label = GetString(item, "label");
        string? command = GetString(item, "command");

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(command))
            return null;

        // Ligne de commande : « command » suivi des « args » (chaînes, ou objets { "value": ... } de VS Code).
        var commandLine = new System.Text.StringBuilder(Substitute(command, root));

        if (item.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement arg in args.EnumerateArray())
            {
                string? value = arg.ValueKind switch
                {
                    JsonValueKind.String => arg.GetString(),
                    JsonValueKind.Object => GetString(arg, "value"),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => arg.GetRawText(),
                    _ => null
                };

                if (value is null)
                    continue;

                commandLine.Append(' ').Append(QuoteIfNeeded(Substitute(value, root)));
            }
        }

        // « cwd » à la racine de la tâche, ou « options.cwd » comme dans VS Code.
        string? cwd = GetString(item, "cwd");
        JsonElement options = default;
        bool hasOptions = item.TryGetProperty("options", out options)
            && options.ValueKind == JsonValueKind.Object;

        if (cwd is null && hasOptions)
            cwd = GetString(options, "cwd");

        Dictionary<string, string>? environment = null;

        if (TryGetObject(item, "env", out JsonElement envElement)
            || (hasOptions && TryGetObject(options, "env", out envElement)))
        {
            environment = new Dictionary<string, string>();

            foreach (JsonProperty variable in envElement.EnumerateObject())
            {
                if (variable.Value.ValueKind == JsonValueKind.String)
                    environment[variable.Name] = Substitute(variable.Value.GetString() ?? "", root);
            }
        }

        return new TaskDefinition(
            label.Trim(),
            commandLine.ToString(),
            cwd is null ? null : Substitute(cwd, root),
            environment,
            source);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static string Substitute(string text, string root) =>
        text.Replace("${workspaceFolder}", root, StringComparison.Ordinal);

    private static string QuoteIfNeeded(string value) =>
        value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
            ? value
            : "\"" + value.Replace("\"", "\\\"") + "\"";

    // ---------- Tâches détectées ----------

    private static IEnumerable<TaskDefinition> Detect(string root)
    {
        // .NET : une solution de préférence, sinon un projet.
        string? dotnetTarget = FirstFile(root, "*.slnx") ?? FirstFile(root, "*.sln") ?? FirstFile(root, "*.csproj");

        if (dotnetTarget is not null)
        {
            string target = QuoteIfNeeded(Path.GetFileName(dotnetTarget));

            yield return Detected(".NET : build", "dotnet build " + target);
            yield return Detected(".NET : test", "dotnet test " + target);
            yield return Detected(".NET : restore", "dotnet restore " + target);
            yield return Detected(".NET : clean", "dotnet clean " + target);
        }

        // npm : un script par entrée de « scripts ».
        foreach (string script in ReadNpmScripts(Path.Combine(root, "package.json")))
            yield return Detected("npm : " + script, "npm run " + QuoteIfNeeded(script));

        if (File.Exists(Path.Combine(root, "Cargo.toml")))
        {
            yield return Detected("cargo : build", "cargo build");
            yield return Detected("cargo : test", "cargo test");
        }

        if (File.Exists(Path.Combine(root, "go.mod")))
        {
            yield return Detected("go : build", "go build ./...");
            yield return Detected("go : test", "go test ./...");
        }
    }

    private static TaskDefinition Detected(string label, string commandLine) =>
        new(label, commandLine, Source: DetectedSource);

    private static string? FirstFile(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadNpmScripts(string packageJson)
    {
        if (!File.Exists(packageJson))
            return Array.Empty<string>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJson), JsonOptions);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && TryGetObject(document.RootElement, "scripts", out JsonElement scripts))
            {
                return scripts.EnumerateObject().Select(p => p.Name).ToList();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // package.json illisible : on ignore simplement les scripts npm.
        }

        return Array.Empty<string>();
    }
}
