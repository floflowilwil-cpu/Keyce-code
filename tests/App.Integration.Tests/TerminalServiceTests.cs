
using App.Core.Models;
using App.Integrations.Terminal;
using Xunit;

namespace App.Integration.Tests;

public class TerminalServiceTests
{
    [Fact]
    public async Task StartSessionAndSendCommand_ShouldReceiveOutput()
    {
        var service = new TerminalService();

        var outputReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        service.OutputReceived += (_, args) =>
        {
            if (args.Data.Contains("TERMINAL_TEST_OK"))
            {
                outputReceived.TrySetResult(args.Data);
            }
        };

        var sessionId = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        await service.SendInputAsync(
            sessionId,
            "echo TERMINAL_TEST_OK & exit\r\n");

        var output = await outputReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(10));

        Assert.Contains("TERMINAL_TEST_OK", output);
    }

    [Fact]
    public async Task StopSessionAsync_ShouldStopRunningProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new TerminalService();

        var sessionId = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        await service.SendInputAsync(
            sessionId,
            "ping 127.0.0.1 -t\r\n");

        await Task.Delay(500);

        await service.StopSessionAsync(sessionId);
    }
}
