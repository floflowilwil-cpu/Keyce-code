using App.Integrations.Debug;

namespace App.Integration.Tests;

public class DebugAdapterRegistryTests
{
    private static readonly DebugAdapterRegistry Registry =
        DebugAdapterRegistry.CreateDefault();

    [Theory]
    [InlineData("app.dll", "csharp")]
    [InlineData("script.py", "python")]
    [InlineData("main.go", "go")]
    [InlineData("program.exe", "c")]
    [InlineData("program", "c")]
    public void Resolve_WithoutLanguage_UsesProgramExtension(
        string program,
        string expectedLanguage)
    {
        DebugAdapterDefinition adapter = Registry.Resolve(null, program);

        Assert.Equal(expectedLanguage, adapter.PrimaryLanguageId);
    }

    [Theory]
    [InlineData("csharp", "csharp")]
    [InlineData("CSharp", "csharp")]
    [InlineData("python", "python")]
    [InlineData("go", "go")]
    [InlineData("c", "c")]
    [InlineData("cpp", "c")]
    [InlineData("rust", "c")]
    public void Resolve_WithLanguage_UsesLanguageId(
        string languageId,
        string expectedPrimary)
    {
        DebugAdapterDefinition adapter = Registry.Resolve(languageId, "whatever");

        Assert.Equal(expectedPrimary, adapter.PrimaryLanguageId);
    }

    [Fact]
    public void Resolve_UnknownLanguage_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => Registry.Resolve("cobol", "main.cob"));
    }

    [Fact]
    public void Resolve_UnknownExtension_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => Registry.Resolve(null, "notes.txt"));
    }

    [Fact]
    public void Resolve_UnknownLanguage_ListsDisplayNamesNotInternalAliases()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Registry.Resolve("cobol", "main.cob"));

        Assert.Contains("C# (.NET)", error.Message);
        Assert.Contains("C / C++ / Rust", error.Message);
        Assert.DoesNotContain("dotnet", error.Message);
        Assert.DoesNotContain("golang", error.Message);
    }

    [Fact]
    public void ValidateProgram_RejectsWrongFileKinds()
    {
        Assert.Throws<ArgumentException>(
            () => Registry.Resolve("csharp", "x").ValidateProgram!("x.exe"));

        Assert.Throws<ArgumentException>(
            () => Registry.Resolve("python", "x").ValidateProgram!("x.txt"));

        Assert.Throws<ArgumentException>(
            () => Registry.Resolve("go", "x").ValidateProgram!("x.txt"));

        Assert.Throws<ArgumentException>(
            () => Registry.Resolve("c", "x").ValidateProgram!("main.c"));
    }

    [Fact]
    public void Registry_CanBeExtendedWithCustomAdapter()
    {
        var custom = new DebugAdapterDefinition
        {
            LanguageIds = ["lua"],
            DisplayName = "Lua",
            DebuggerName = "fake-lua-debugger",
            AdapterId = "lua",
            ProgramExtensions = [".lua"],
            MissingMessage = "absent",
            InstallHint = "installez-le",
            CreateCommand = _ => null,
            CreateLaunchArguments = context => new System.Text.Json.Nodes.JsonObject
            {
                ["program"] = context.Program
            }
        };

        var registry = new DebugAdapterRegistry([custom]);

        Assert.Same(custom, registry.Resolve(null, "script.lua"));
    }
}
