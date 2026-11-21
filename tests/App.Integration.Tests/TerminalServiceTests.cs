
using System.IO;
using System.Text;
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
            return;

        var service = new TerminalService();

        var outputReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var output = new StringBuilder();
        var outputLock = new object();

        service.OutputReceived += (_, args) =>
        {
            lock (outputLock)
            {
                output.Append(args.Data);

                string currentOutput = output.ToString();

                if (currentOutput.Contains("80235"))
                    outputReceived.TrySetResult(currentOutput);
            }
        };

        var sessionId = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        try
        {
            await service.SendInputAsync(
                sessionId,
                "set /a 12345+67890 & exit\r\n");

            var result = await outputReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            Assert.Contains("80235", result);
        }
        finally
        {
            await service.StopSessionAsync(sessionId);
        }
    }

    [Fact]
    public async Task StopSessionAsync_ShouldStopRunningProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new TerminalService();

        var sessionId = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        try
        {
            await service.SendInputAsync(
                sessionId,
                "ping 127.0.0.1 -t\r\n");

            await Task.Delay(500);

            await service.StopSessionAsync(sessionId);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SendInputAsync(
                    sessionId,
                    "echo AFTER_STOP\r\n"));
        }
        finally
        {
            await service.StopSessionAsync(sessionId);
        }
    }

    [Fact]
    public async Task SendInputWithoutNewline_ShouldWaitForLineEnding()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new TerminalService();

        var outputReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var output = new StringBuilder();
        var outputLock = new object();

        service.OutputReceived += (_, args) =>
        {
            lock (outputLock)
            {
                output.Append(args.Data);

                string currentOutput = output.ToString();

                if (currentOutput.Contains("80235"))
                    outputReceived.TrySetResult(currentOutput);
            }
        };

        var sessionId = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        try
        {
            await service.SendInputAsync(
                sessionId,
                "set /a 12345+67890");

            await Task.Delay(300);

            lock (outputLock)
            {
                Assert.DoesNotContain("80235", output.ToString());
            }

            await service.SendInputAsync(
                sessionId,
                "\r\nexit\r\n");

            var result = await outputReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            Assert.Contains("80235", result);
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
            return;

        var service = new TerminalService();

        var firstOutput = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var secondOutput = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstBuffer = new StringBuilder();
        var secondBuffer = new StringBuilder();

        var outputLock = new object();

        var firstSession = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        var secondSession = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        service.OutputReceived += (_, args) =>
        {
            lock (outputLock)
            {
                if (args.SessionId == firstSession)
                {
                    firstBuffer.Append(args.Data);

                    string currentOutput = firstBuffer.ToString();

                    if (currentOutput.Contains("101"))
                        firstOutput.TrySetResult(currentOutput);
                }

                if (args.SessionId == secondSession)
                {
                    secondBuffer.Append(args.Data);

                    string currentOutput = secondBuffer.ToString();

                    if (currentOutput.Contains("202"))
                        secondOutput.TrySetResult(currentOutput);
                }
            }
        };

        try
        {
            await service.SendInputAsync(
                firstSession,
                "set /a 100+1\r\n");

            await service.SendInputAsync(
                secondSession,
                "set /a 200+2\r\n");

            var results = await Task.WhenAll(
                firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                secondOutput.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Contains("101", results[0]);
            Assert.Contains("202", results[1]);
        }
        finally
        {
            await service.StopSessionAsync(firstSession);
            await service.StopSessionAsync(secondSession);
        }
    }

    [Fact]
    public async Task NaturalProcessExit_ShouldRemoveSession()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new TerminalService();

        var sessionId = await service.StartSessionAsync(
            new TerminalStartOptions(Shell: "cmd.exe"));

        try
        {
            await service.SendInputAsync(
                sessionId,
                "exit\r\n");

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(10));

            while (true)
            {
                try
                {
                    await service.SendInputAsync(
                        sessionId,
                        "\r\n",
                        timeout.Token);
                }
                catch (ObjectDisposedException)
                {
                    // Le PTY est fermé, mais le nettoyage de la session
                    // peut ne pas être terminé.
                }
                catch (IOException)
                {
                    // Le transport PTY est en cours de fermeture.
                }
                catch (InvalidOperationException ex)
                {
                    // La session doit avoir été retirée du dictionnaire.
                    Assert.Equal(
                        "Session de terminal introuvable.",
                        ex.Message);

                    return;
                }

                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(
                "La session n'a pas été retirée après la fermeture naturelle du processus.");
        }
        finally
        {
            await service.StopSessionAsync(sessionId);
        }
    }
}
