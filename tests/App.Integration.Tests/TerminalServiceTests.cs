
using App.Core.Models;
using App.Integrations.Terminal;
using Xunit;

namespace App.Integration.Tests;

public class TerminalServiceTests
{
    [Fact]
    public async Task StartSessionAndSendCommand_ShouldReceiveOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

    [Fact]
    public async Task SendInputWithoutNewline_ShouldWaitForLineEnding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new TerminalService();

        var outputReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        service.OutputReceived += (_, args) =>
        {
            if (args.Data.Contains("NO_NEWLINE_TEST"))
            {
                outputReceived.TrySetResult(args.Data);
            }
        };

        var sessionId = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        try
        {
            await service.SendInputAsync(
                sessionId,
                "echo NO_NEWLINE_TEST");

            await Task.Delay(300);

            Assert.False(outputReceived.Task.IsCompleted);

            await service.SendInputAsync(
                sessionId,
                "\r\nexit\r\n");

            var output = await outputReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            Assert.Contains("NO_NEWLINE_TEST", output);
        }
        finally
        {
            await service.StopSessionAsync(sessionId);
        }
    }

    [Fact]
    public async Task MultipleSessions_ShouldReceiveIndependentOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new TerminalService();

        var firstOutput = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var secondOutput = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstSession = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        var secondSession = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        service.OutputReceived += (_, args) =>
        {
            if (args.SessionId == firstSession &&
                args.Data.Contains("SESSION_A"))
            {
                firstOutput.TrySetResult(args.Data);
            }

            if (args.SessionId == secondSession &&
                args.Data.Contains("SESSION_B"))
            {
                secondOutput.TrySetResult(args.Data);
            }
        };

        try
        {
            await service.SendInputAsync(
                firstSession,
                "echo SESSION_A\r\n");

            await service.SendInputAsync(
                secondSession,
                "echo SESSION_B\r\n");

            var results = await Task.WhenAll(
                firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                secondOutput.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Contains("SESSION_A", results[0]);
            Assert.Contains("SESSION_B", results[1]);
        }
        finally
        {
            await service.StopSessionAsync(firstSession);
            await service.StopSessionAsync(secondSession);
        }
    }
}
