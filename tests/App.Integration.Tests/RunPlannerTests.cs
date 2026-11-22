using App.Core.Models;
using App.Integrations.Execution;

namespace App.Integration.Tests;

public class RunPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyce-runplanner-" + Guid.NewGuid().ToString("N"));

    public RunPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nettoyage best-effort.
        }
    }

    private static string? Everything(string tool) => Path.Combine("fake", tool);

    private static string? Nothing(string tool) => null;

    private string CreateFile(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(_root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static string Args(RunStep step) =>
        string.Join("|", step.Options.Arguments ?? Array.Empty<string>());

    [Fact]
    public void PlanRun_Python_UsesInterpreterAndUnbufferedFlag()
    {
        string file = CreateFile("script.py");

        RunPlan plan = new RunPlanner(Everything).PlanRun(file);

        Assert.Equal("python", plan.LanguageId);
        RunStep step = Assert.Single(plan.Steps);
        Assert.Equal("-u|" + file, Args(step));
        Assert.Equal(Path.GetDirectoryName(file), step.Options.WorkingDirectory);
    }

    [Fact]
    public void PlanRun_JavaScript_UsesNode()
    {
        RunPlan plan = new RunPlanner(Everything).PlanRun(CreateFile("app.js"));

        Assert.Equal("javascript", plan.LanguageId);
        Assert.EndsWith("node", Assert.Single(plan.Steps).Options.Executable);
    }

    [Fact]
    public void PlanRun_CSharpInsideProject_RunsNearestProject()
    {
        string project = CreateFile("app/App.csproj");
        string source = CreateFile("app/src/Program.cs");

        RunPlan plan = new RunPlanner(Everything).PlanRun(source);

        Assert.Equal("csharp", plan.LanguageId);
        RunStep step = Assert.Single(plan.Steps);
        Assert.Equal("run|--project|" + project, Args(step));
        Assert.Equal(Path.GetDirectoryName(project), step.Options.WorkingDirectory);
    }

    [Fact]
    public void PlanRun_CSharpWithoutProject_RunsSingleFile()
    {
        string source = CreateFile("solo/Program.cs");

        RunPlan plan = new RunPlanner(Everything).PlanRun(source);

        Assert.Equal("run|" + source, Args(Assert.Single(plan.Steps)));
    }

    [Fact]
    public void PlanRun_C_CompilesThenRuns()
    {
        RunPlan plan = new RunPlanner(Everything).PlanRun(CreateFile("hello.c"));

        Assert.Equal("c", plan.LanguageId);
        Assert.Equal(2, plan.Steps.Count);
        Assert.EndsWith("gcc", plan.Steps[0].Options.Executable);
        Assert.Contains("-o", plan.Steps[0].Options.Arguments!);
        Assert.Contains("hello", plan.Steps[1].Options.Executable);
    }

    [Fact]
    public void PlanRun_UnsupportedExtension_Throws()
    {
        var planner = new RunPlanner(Everything);

        Assert.Throws<NotSupportedException>(() => planner.PlanRun(CreateFile("notes.txt")));
    }

    [Fact]
    public void PlanRun_MissingInterpreter_Throws()
    {
        var planner = new RunPlanner(Nothing);

        Assert.Throws<InvalidOperationException>(() => planner.PlanRun(CreateFile("script.py")));
    }

    [Fact]
    public void PlanRun_MissingFile_Throws()
    {
        var planner = new RunPlanner(Everything);

        Assert.Throws<FileNotFoundException>(
            () => planner.PlanRun(Path.Combine(_root, "absent.py")));
    }

    [Fact]
    public void PlanDebugBuild_CSharpProject_BuildsInDebug()
    {
        string project = CreateFile("app/App.csproj");
        string source = CreateFile("app/Program.cs");

        RunPlan plan = new RunPlanner(Everything).PlanDebugBuild(source);

        Assert.Equal("build|" + project + "|-c|Debug|-nologo", Args(Assert.Single(plan.Steps)));
    }

    [Fact]
    public void PlanDebugBuild_NonCSharp_Throws()
    {
        var planner = new RunPlanner(Everything);

        Assert.Throws<NotSupportedException>(() => planner.PlanDebugBuild(CreateFile("script.py")));
    }

    [Fact]
    public void FindDebugAssembly_ReturnsCompiledDll()
    {
        CreateFile("app/App.csproj");
        string source = CreateFile("app/Program.cs");
        string dll = CreateFile("app/bin/Debug/net10.0/App.dll");

        string found = new RunPlanner(Everything).FindDebugAssembly(source);

        Assert.Equal(dll, found);
    }

    [Fact]
    public void FindDebugAssembly_WithoutBuild_Throws()
    {
        CreateFile("app/App.csproj");
        string source = CreateFile("app/Program.cs");
        var planner = new RunPlanner(Everything);

        Assert.Throws<FileNotFoundException>(() => planner.FindDebugAssembly(source));
    }

    [Fact]
    public void PlanDebug_CSharp_BuildsAndLeavesProgramToFindDebugAssembly()
    {
        CreateFile("app/App.csproj");
        string source = CreateFile("app/Program.cs");

        DebugPlan plan = new RunPlanner(Everything).PlanDebug(source);

        Assert.Equal("csharp", plan.LanguageId);
        Assert.NotNull(plan.Build);
        Assert.Null(plan.Program);
    }

    [Fact]
    public void PlanDebug_Python_NeedsNoBuild()
    {
        string file = CreateFile("script.py");

        DebugPlan plan = new RunPlanner(Everything).PlanDebug(file);

        Assert.Equal("python", plan.LanguageId);
        Assert.Null(plan.Build);
        Assert.Equal(file, plan.Program);
    }

    [Fact]
    public void PlanDebug_Python_WithoutInterpreter_Throws()
    {
        string file = CreateFile("script.py");

        Assert.Throws<InvalidOperationException>(
            () => new RunPlanner(Nothing).PlanDebug(file));
    }

    [Fact]
    public void PlanDebug_Go_NeedsNoBuild()
    {
        string file = CreateFile("main.go");

        DebugPlan plan = new RunPlanner(Everything).PlanDebug(file);

        Assert.Equal("go", plan.LanguageId);
        Assert.Null(plan.Build);
        Assert.Equal(file, plan.Program);
    }

    [Fact]
    public void PlanDebug_C_CompilesWithDebugSymbols()
    {
        string file = CreateFile("main.c");

        DebugPlan plan = new RunPlanner(Everything).PlanDebug(file);

        Assert.Equal("c", plan.LanguageId);
        RunStep step = Assert.Single(plan.Build!.Steps);
        Assert.Contains("-g", step.Options.Arguments!);
        Assert.NotNull(plan.Program);
        Assert.StartsWith("main", Path.GetFileName(plan.Program!));
    }

    [Fact]
    public void PlanDebug_RustCargoProject_UsesPackageNameFromCargoToml()
    {
        File.WriteAllText(
            CreateFile("proj/Cargo.toml"),
            "[package]\nname = \"demo\"\nversion = \"0.1.0\"\n");
        string source = CreateFile("proj/src/main.rs");

        DebugPlan plan = new RunPlanner(Everything).PlanDebug(source);

        Assert.Equal("rust", plan.LanguageId);
        Assert.Equal("build", Args(Assert.Single(plan.Build!.Steps)));

        string expected = Path.Combine(
            _root,
            "proj",
            "target",
            "debug",
            "demo" + (OperatingSystem.IsWindows() ? ".exe" : ""));

        Assert.Equal(expected, plan.Program);
    }

    [Fact]
    public void PlanDebug_UnsupportedExtension_Throws()
    {
        var planner = new RunPlanner(Everything);

        Assert.Throws<NotSupportedException>(
            () => planner.PlanDebug(CreateFile("notes.txt")));
    }
}
