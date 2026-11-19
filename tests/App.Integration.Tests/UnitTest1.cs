
using App.Core.Models;
using App.Integrations.Terminal;

namespace App.Integration.Tests;

public class TerminalServiceTests
{
    [Fact]
    public async Task StartSessionAndSendCommand_ShouldReceiveOutput()
    {
        // Ce test utilise cmd.exe : il est destiné à Windows.
        if (!OperatingSystem.IsWindows())
            return;

        // Arrange
        var service = new TerminalService();

        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        service.OutputReceived += (_, args) =>
        {
            if (args.Data.Contains("TERMINAL_TEST_OK"))
            {
                completion.TrySetResult(args.Data);
            }
        };

        // Act
        var sessionId = await service.StartSessionAsync(
            new TerminalStartOptions(
                Shell: "cmd.exe",
                WorkingDirectory: Environment.CurrentDirectory));

        await service.SendInputAsync(
            sessionId,
            "echo TERMINAL_TEST_OK & exit");

        // Assert
        var output = await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(10));

        Assert.Contains("TERMINAL_TEST_OK", output);
    }
}
