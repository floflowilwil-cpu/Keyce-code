
using System.Diagnostics;
using App.Core.Models;
using App.Integrations.Debug;

namespace App.Integration.Tests;

public class DebugServiceTests
{
    [Fact]
    public async Task StartDebugSetBreakpointAndStop_ShouldHitBreakpoint()
    {
        string directory = CreateTemporaryDirectory();
        var service = new DebugService();
        DebugSessionId? sessionId = null;

        try
        {
            string sourceFile = await CreateDebuggeeAsync(directory);

            string dllPath = Path.Combine(
                directory, "bin", "Debug", "net10.0", "Debuggee.dll");

            sessionId = await service.StartDebugAsync(
                new DebugStartOptions(dllPath, WorkingDirectory: directory));

            await service.SetBreakpointAsync(
                sessionId.Value,
                new Breakpoint(sourceFile, 2));

            await service.StepAsync(sessionId.Value, StepKind.Continue);

            DebugStopInfo stop = await service.WaitForStopAsync(sessionId.Value);

            Assert.Equal("breakpoint", stop.Reason);
            Assert.Equal(2, stop.Line);
            Assert.NotNull(stop.SourcePath);

            Assert.Equal(
                Path.GetFullPath(sourceFile),
                Path.GetFullPath(stop.SourcePath));
        }
        finally
        {
            await StopSessionIfNeededAsync(service, sessionId);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StartDebugAndWaitForNaturalExit_ShouldEndSession()
    {
        string directory = CreateTemporaryDirectory();
        var service = new DebugService();
        DebugSessionId? sessionId = null;

        try
        {
            await CreateDebuggeeAsync(directory);

            string dllPath = Path.Combine(
                directory, "bin", "Debug", "net10.0", "Debuggee.dll");

            sessionId = await service.StartDebugAsync(
                new DebugStartOptions(dllPath, WorkingDirectory: directory));

            await service.StepAsync(sessionId.Value, StepKind.Continue);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.WaitForStopAsync(sessionId.Value));
        }
        finally
        {
            await StopSessionIfNeededAsync(service, sessionId);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StepOver_ShouldMoveToNextLine()
    {
        string directory = CreateTemporaryDirectory();
        var service = new DebugService();
        DebugSessionId? sessionId = null;

        try
        {
            string sourceFile = await CreateDebuggeeAsync(directory);

            string dllPath = Path.Combine(
                directory, "bin", "Debug", "net10.0", "Debuggee.dll");

            sessionId = await service.StartDebugAsync(
                new DebugStartOptions(dllPath, WorkingDirectory: directory));

            await service.SetBreakpointAsync(
                sessionId.Value,
                new Breakpoint(sourceFile, 2));

            await service.StepAsync(sessionId.Value, StepKind.Continue);

            DebugStopInfo breakpointStop =
                await service.WaitForStopAsync(sessionId.Value);

            Assert.Equal("breakpoint", breakpointStop.Reason);
            Assert.Equal(2, breakpointStop.Line);

            await service.StepAsync(sessionId.Value, StepKind.Over);

            DebugStopInfo stepStop =
                await service.WaitForStopAsync(sessionId.Value);

            Assert.Equal("step", stepStop.Reason);
            Assert.Equal(3, stepStop.Line);
        }
        finally
        {
            await StopSessionIfNeededAsync(service, sessionId);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StepInto_ShouldEnterCalledFunction()
    {
        string directory = CreateTemporaryDirectory();
        var service = new DebugService();
        DebugSessionId? sessionId = null;

        try
        {
            string sourceFile = await CreateDebuggeeAsync(
                directory,
                includeHelper: true);

            string dllPath = Path.Combine(
                directory, "bin", "Debug", "net10.0", "Debuggee.dll");

            sessionId = await service.StartDebugAsync(
                new DebugStartOptions(dllPath, WorkingDirectory: directory));

            await service.SetBreakpointAsync(
                sessionId.Value,
                new Breakpoint(sourceFile, 7));

            await service.StepAsync(sessionId.Value, StepKind.Continue);

            DebugStopInfo breakpointStop =
                await service.WaitForStopAsync(sessionId.Value);

            Assert.Equal("breakpoint", breakpointStop.Reason);
            Assert.Equal(7, breakpointStop.Line);

            await service.StepAsync(sessionId.Value, StepKind.Into);

            DebugStopInfo stepStop =
                await service.WaitForStopAsync(sessionId.Value);

            Assert.Equal("step", stepStop.Reason);

            Assert.NotNull(stepStop.Line);
            Assert.InRange(stepStop.Line.Value, 2, 3);

            Assert.NotNull(stepStop.SourcePath);
            Assert.Equal(
                Path.GetFullPath(sourceFile),
                Path.GetFullPath(stepStop.SourcePath));
        }
        finally
        {
            await StopSessionIfNeededAsync(service, sessionId);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StepOut_ShouldReturnToCallingFunction()
    {
        string directory = CreateTemporaryDirectory();
        var service = new DebugService();
        DebugSessionId? sessionId = null;

        try
        {
            string sourceFile = await CreateDebuggeeAsync(
                directory,
                includeHelper: true);

            string dllPath = Path.Combine(
                directory, "bin", "Debug", "net10.0", "Debuggee.dll");

            sessionId = await service.StartDebugAsync(
                new DebugStartOptions(dllPath, WorkingDirectory: directory));

            await service.SetBreakpointAsync(
                sessionId.Value,
                new Breakpoint(sourceFile, 3));

            await service.StepAsync(sessionId.Value, StepKind.Continue);

            DebugStopInfo breakpointStop =
                await service.WaitForStopAsync(sessionId.Value);

            Assert.Equal("breakpoint", breakpointStop.Reason);
            Assert.Equal(3, breakpointStop.Line);

            await service.StepAsync(sessionId.Value, StepKind.Out);

            DebugStopInfo stepStop =
                await service.WaitForStopAsync(sessionId.Value);

            Assert.Equal("step", stepStop.Reason);
            Assert.Equal(7, stepStop.Line);

            Assert.NotNull(stepStop.SourcePath);
            Assert.Equal(
                Path.GetFullPath(sourceFile),
                Path.GetFullPath(stepStop.SourcePath));
        }
        finally
        {
            await StopSessionIfNeededAsync(service, sessionId);
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"KeyceDebugTest_{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> CreateDebuggeeAsync(
        string directory,
        bool includeHelper = false)
    {
        string projectFile = Path.Combine(directory, "Debuggee.csproj");
        string sourceFile = Path.Combine(directory, "Program.cs");

        await File.WriteAllTextAsync(
            projectFile,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        string sourceCode = includeHelper
            ?
            """
            static void Helper()
            {
                Console.WriteLine("INSIDE_HELPER");
            }

            Console.WriteLine("BEFORE");
            Helper();
            Console.WriteLine("AFTER");
            """
            :
            """
            Console.WriteLine("DEBUG_TEST_START");
            Console.WriteLine("BREAKPOINT_TEST");
            Console.WriteLine("DEBUG_TEST_END");
            """;

        await File.WriteAllTextAsync(sourceFile, sourceCode);

        var buildInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        buildInfo.ArgumentList.Add("build");
        buildInfo.ArgumentList.Add(projectFile);

        using var buildProcess = Process.Start(buildInfo)
            ?? throw new InvalidOperationException(
                "Impossible de démarrer la compilation du programme test.");

        Task<string> outputTask =
            buildProcess.StandardOutput.ReadToEndAsync();

        Task<string> errorTask =
            buildProcess.StandardError.ReadToEndAsync();

        await buildProcess.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(60));

        string output = await outputTask;
        string error = await errorTask;

        Assert.True(
            buildProcess.ExitCode == 0,
            $"Compilation échouée.\n{output}\n{error}");

        return sourceFile;
    }

    private static async Task StopSessionIfNeededAsync(
        DebugService service,
        DebugSessionId? sessionId)
    {
        if (!sessionId.HasValue)
            return;

        try
        {
            await service.StopDebugAsync(sessionId.Value);
        }
        catch (InvalidOperationException)
        {
            // Session déjà terminée ou supprimée.
        }
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
