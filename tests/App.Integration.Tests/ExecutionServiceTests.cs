
using System.Collections.Concurrent;
using System.Diagnostics;
using App.Core.Models;
using App.Integrations.Execution;

namespace App.Integration.Tests;

public class ExecutionServiceTests
{
    [Fact]
    public async Task StartExecutionAndWait_ShouldCaptureOutputAndReturnExitCode()
    {
        string directory = CreateTemporaryDirectory();
        var service = new ExecutionService();

        var receivedOutput = new ConcurrentQueue<ExecutionOutputEventArgs>();
        var outputReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        service.OutputReceived += (_, args) =>
        {
            receivedOutput.Enqueue(args);

            bool hasStandardOutput = receivedOutput.Any(
                item => item.Stream == ExecutionOutputStream.StandardOutput);

            bool hasStandardError = receivedOutput.Any(
                item => item.Stream == ExecutionOutputStream.StandardError);

            if (hasStandardOutput && hasStandardError)
                outputReceived.TrySetResult(true);
        };

        try
        {
            string dllPath = await CreateExecutableAsync(
                directory,
                """
                Console.WriteLine(args[0]);
                Console.Error.WriteLine(
                    Environment.GetEnvironmentVariable("KEYCE_EXEC_TEST"));
                return 7;
                """);

            ExecutionSessionId sessionId =
                await service.StartExecutionAsync(
                    new ExecutionStartOptions(
                        "dotnet",
                        Arguments: [dllPath, "hello world"],
                        WorkingDirectory: directory,
                        Environment: new Dictionary<string, string>
                        {
                            ["KEYCE_EXEC_TEST"] = "environment-value"
                        }));

            await outputReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));

            ExecutionResult result =
                await service.WaitForExitAsync(sessionId)
                    .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(sessionId, result.SessionId);
            Assert.Equal(7, result.ExitCode);

            Assert.Contains(
                receivedOutput,
                item =>
                    item.Stream == ExecutionOutputStream.StandardOutput &&
                    item.Data.Contains("hello world"));

            Assert.Contains(
                receivedOutput,
                item =>
                    item.Stream == ExecutionOutputStream.StandardError &&
                    item.Data.Contains("environment-value"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StopExecution_ShouldTerminateRunningProcess()
    {
        string directory = CreateTemporaryDirectory();
        var service = new ExecutionService();

        try
        {
            string dllPath = await CreateExecutableAsync(
                directory,
                """
                Console.WriteLine("PROCESS_STARTED");
                await Task.Delay(TimeSpan.FromMinutes(5));
                """);

            ExecutionSessionId sessionId =
                await service.StartExecutionAsync(
                    new ExecutionStartOptions(
                        "dotnet",
                        Arguments: [dllPath],
                        WorkingDirectory: directory));

            await service.StopExecutionAsync(sessionId)
                .WaitAsync(TimeSpan.FromSeconds(15));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.WaitForExitAsync(sessionId));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task WaitForExitAsync_CalledConcurrently_ShouldReturnSameResult()
    {
        string directory = CreateTemporaryDirectory();
        var service = new ExecutionService();

        try
        {
            string dllPath = await CreateExecutableAsync(
                directory,
                """
                await Task.Delay(500);
                return 12;
                """);

            ExecutionSessionId sessionId =
                await service.StartExecutionAsync(
                    new ExecutionStartOptions(
                        "dotnet",
                        Arguments: [dllPath],
                        WorkingDirectory: directory));

            Task<ExecutionResult>[] waits = Enumerable.Range(0, 5)
                .Select(_ => service.WaitForExitAsync(sessionId))
                .ToArray();

            ExecutionResult[] results = await Task.WhenAll(waits)
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.All(
                results,
                result =>
                {
                    Assert.Equal(sessionId, result.SessionId);
                    Assert.Equal(12, result.ExitCode);
                });
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StopExecution_CalledConcurrently_ShouldCompleteSafely()
    {
        string directory = CreateTemporaryDirectory();
        var service = new ExecutionService();

        try
        {
            string dllPath = await CreateExecutableAsync(
                directory,
                """
                Console.WriteLine("PROCESS_STARTED");
                await Task.Delay(TimeSpan.FromMinutes(5));
                """);

            ExecutionSessionId sessionId =
                await service.StartExecutionAsync(
                    new ExecutionStartOptions(
                        "dotnet",
                        Arguments: [dllPath],
                        WorkingDirectory: directory));

            Task[] stops = Enumerable.Range(0, 5)
                .Select(_ => service.StopExecutionAsync(sessionId))
                .ToArray();

            await Task.WhenAll(stops)
                .WaitAsync(TimeSpan.FromSeconds(15));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.WaitForExitAsync(sessionId));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task WaitAndStopExecution_CalledConcurrently_ShouldCompleteSafely()
    {
        string directory = CreateTemporaryDirectory();
        var service = new ExecutionService();

        try
        {
            string dllPath = await CreateExecutableAsync(
                directory,
                """
                await Task.Delay(TimeSpan.FromMinutes(5));
                """);

            ExecutionSessionId sessionId =
                await service.StartExecutionAsync(
                    new ExecutionStartOptions(
                        "dotnet",
                        Arguments: [dllPath],
                        WorkingDirectory: directory));

            Task<ExecutionResult> wait =
                service.WaitForExitAsync(sessionId);

            Task stop = service.StopExecutionAsync(sessionId);

            await stop.WaitAsync(TimeSpan.FromSeconds(15));

            ExecutionResult result = await wait
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(sessionId, result.SessionId);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task WaitForExitAsync_WhenCancelled_ShouldNotStopProcess()
    {
        string directory = CreateTemporaryDirectory();
        var service = new ExecutionService();

        try
        {
            string dllPath = await CreateExecutableAsync(
                directory,
                """
                await Task.Delay(1000);
                return 5;
                """);

            ExecutionSessionId sessionId =
                await service.StartExecutionAsync(
                    new ExecutionStartOptions(
                        "dotnet",
                        Arguments: [dllPath],
                        WorkingDirectory: directory));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.WaitForExitAsync(
                    sessionId,
                    cancellation.Token));

            ExecutionResult result =
                await service.WaitForExitAsync(sessionId)
                    .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(sessionId, result.SessionId);
            Assert.Equal(5, result.ExitCode);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StartExecution_WithMissingWorkingDirectory_ShouldThrow()
    {
        var service = new ExecutionService();

        string missingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"KeyceMissing_{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => service.StartExecutionAsync(
                new ExecutionStartOptions(
                    "dotnet",
                    WorkingDirectory: missingDirectory)));
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"KeyceExecutionTest_{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        return directory;
    }

    private static async Task<string> CreateExecutableAsync(
        string directory,
        string sourceCode)
    {
        string projectFile = Path.Combine(
            directory,
            "ExecutionTest.csproj");

        string sourceFile = Path.Combine(
            directory,
            "Program.cs");

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

        await File.WriteAllTextAsync(sourceFile, sourceCode);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectFile);
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("quiet");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Impossible de démarrer la compilation du programme test.");

        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();

        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(60));

        string output = await outputTask;
        string error = await errorTask;

        Assert.True(
            process.ExitCode == 0,
            $"Compilation du programme test échouée.\n{output}\n{error}");

        return Path.Combine(
            directory,
            "bin",
            "Debug",
            "net10.0",
            "ExecutionTest.dll");
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
