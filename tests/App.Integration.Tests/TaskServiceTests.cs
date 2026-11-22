using App.Core.Errors;
using App.Core.Models;
using App.Integrations.Execution;
using App.Integrations.Tasks;

namespace App.Integration.Tests;

public class TaskServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "keyce-task-tests-" + Guid.NewGuid().ToString("N"));

    private readonly TaskService _service = new();

    public TaskServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void GetTasks_EmptyFolder_ReturnsNothing()
    {
        Assert.Empty(_service.GetTasks(_root));
    }

    [Fact]
    public void GetTasks_ReadsKeyceConfig_WithArgsCwdAndEnv()
    {
        Write(".keyce/tasks.json", """
            {
              // commentaire autorisé
              "tasks": [
                {
                  "label": "Hello",
                  "command": "echo",
                  "args": ["a b", "c"],
                  "cwd": "${workspaceFolder}/sub",
                  "env": { "KEYCE_X": "${workspaceFolder}" },
                },
                { "label": "Sans commande" }
              ]
            }
            """);

        TaskDefinition task = Assert.Single(_service.GetTasks(_root));

        Assert.Equal("Hello", task.Label);
        Assert.Equal("echo \"a b\" c", task.CommandLine);
        Assert.Equal(_root + "/sub", task.WorkingDirectory);
        Assert.Equal(_root, task.Environment!["KEYCE_X"]);
        Assert.Equal(".keyce/tasks.json", task.Source);
    }

    [Fact]
    public void GetTasks_ReadsVsCodeOptionsCwd()
    {
        Write(".vscode/tasks.json", """
            { "version": "2.0.0", "tasks": [
              { "label": "Build", "type": "shell", "command": "make", "options": { "cwd": "build" } }
            ] }
            """);

        TaskDefinition task = Assert.Single(_service.GetTasks(_root));

        Assert.Equal("make", task.CommandLine);
        Assert.Equal("build", task.WorkingDirectory);
        Assert.Equal(".vscode/tasks.json", task.Source);
    }

    [Fact]
    public void GetTasks_DetectsDotnetAndNpm_AndKeyceConfigWinsOnSameLabel()
    {
        Write("App.sln", "");
        Write("package.json", """{ "scripts": { "build": "tsc", "lint": "eslint ." } }""");
        Write(".keyce/tasks.json", """
            { "tasks": [ { "label": ".NET : build", "command": "dotnet build -c Release" } ] }
            """);

        IReadOnlyList<TaskDefinition> tasks = _service.GetTasks(_root);

        Assert.Equal("dotnet build -c Release", tasks.Single(t => t.Label == ".NET : build").CommandLine);
        Assert.Contains(tasks, t => t.Label == ".NET : test" && t.CommandLine == "dotnet test App.sln");
        Assert.Contains(tasks, t => t.Label == "npm : lint" && t.CommandLine == "npm run lint");
    }

    [Fact]
    public void GetTasks_InvalidJson_ThrowsAppException()
    {
        Write(".keyce/tasks.json", "{ pas du json");

        var ex = Assert.Throws<AppException>(() => _service.GetTasks(_root));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void EnsureConfigFile_CreatesTemplateOnce_AndItIsReadable()
    {
        string path = _service.EnsureConfigFile(_root);
        string first = File.ReadAllText(path);

        Assert.Equal(_service.GetConfigFilePath(_root), path);
        Assert.Single(_service.GetTasks(_root));

        File.WriteAllText(path, first + "\n");
        _service.EnsureConfigFile(_root);

        Assert.Equal(first + "\n", File.ReadAllText(path));
    }

    [Fact]
    public void CreatePlan_MissingWorkingDirectory_ThrowsAppException()
    {
        var task = new TaskDefinition("T", "echo x", WorkingDirectory: "n'existe-pas");

        var ex = Assert.Throws<AppException>(() => _service.CreatePlan(task, _root));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task CreatePlan_RunsThroughTheShell_AndReturnsExitCodeAndOutput()
    {
        var task = new TaskDefinition(
            "Echo",
            "echo salut-keyce && exit 3",
            Environment: new Dictionary<string, string> { ["KEYCE_T"] = "1" });

        RunPlan plan = _service.CreatePlan(task, _root);
        RunStep step = Assert.Single(plan.Steps);

        var service = new ExecutionService();
        var output = new System.Text.StringBuilder();
        service.OutputReceived += (_, e) =>
        {
            lock (output) output.Append(e.Data);
        };

        ExecutionSessionId id = await service.StartExecutionAsync(step.Options);
        ExecutionResult result = await service.WaitForExitAsync(id)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(3, result.ExitCode);
        lock (output) Assert.Contains("salut-keyce", output.ToString());
    }
}
