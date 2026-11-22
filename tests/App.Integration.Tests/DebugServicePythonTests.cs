using System.Diagnostics;
using App.Core.Models;
using App.Integrations.Debug;

namespace App.Integration.Tests;

/// <summary>Test de bout en bout du débogage Python (ignoré si Python ou debugpy sont absents).</summary>
public class DebugServicePythonTests
{
    private static bool IsDebugpyInstalled()
    {
        foreach (string python in new[] { "python", "python3" })
        {
            try
            {
                var startInfo = new ProcessStartInfo(python)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("import debugpy");

                using Process? process = Process.Start(startInfo);

                if (process is null)
                    continue;

                if (!process.WaitForExit(15_000))
                {
                    process.Kill(entireProcessTree: true);
                    continue;
                }

                if (process.ExitCode == 0)
                    return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Cet exécutable n'existe pas : on essaie le suivant.
            }
        }

        return false;
    }

    [Fact]
    public async Task Python_StartBreakpointAndStop_ShouldHitBreakpoint()
    {
        if (!IsDebugpyInstalled())
            return; // Python + debugpy requis : python -m pip install debugpy

        string directory = Path.Combine(
            Path.GetTempPath(),
            "keyce-pydebug-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        var service = new DebugService();
        DebugSessionId? sessionId = null;

        try
        {
            string script = Path.Combine(directory, "app.py");

            await File.WriteAllTextAsync(script, "x = 1\ny = x + 1\nprint(y)\n");

            sessionId = await service.StartDebugAsync(
                new DebugStartOptions(script, WorkingDirectory: directory));

            await service.SetBreakpointAsync(
                sessionId.Value,
                new Breakpoint(script, 2));

            await service.StepAsync(sessionId.Value, StepKind.Continue);

            DebugStopInfo stop = await service.WaitForStopAsync(sessionId.Value);

            Assert.Equal("breakpoint", stop.Reason);
            Assert.Equal(2, stop.Line);
        }
        finally
        {
            if (sessionId is { } id)
            {
                try
                {
                    await service.StopDebugAsync(id);
                }
                catch (Exception)
                {
                    // Nettoyage best-effort.
                }
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nettoyage best-effort.
            }
        }
    }
}
