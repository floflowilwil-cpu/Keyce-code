
using System.Collections.Concurrent;
using System.Diagnostics;
using App.Core.Contracts;
using App.Core.Models;

namespace App.Integrations.Terminal;

public sealed class TerminalService : ITerminalService
{
    private sealed class TerminalSession(Process process)
    {
        public Process Process { get; } = process;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool IsDisposed { get; set; }
    }

    private readonly ConcurrentDictionary<TerminalSessionId, TerminalSession>
        _sessions = new();

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public Task<TerminalSessionId> StartSessionAsync(
        TerminalStartOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        string shell = options.Shell ??
            (OperatingSystem.IsWindows()
                ? "powershell.exe"
                : "/bin/bash");

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = options.WorkingDirectory
                ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (options.Environment is not null)
        {
            foreach (var variable in options.Environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        var process = new Process
        {
            StartInfo = startInfo
        };

        bool started = false;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Impossible de démarrer le terminal.");
            }

            started = true;

            var sessionId = TerminalSessionId.New();
            var session = new TerminalSession(process);

            if (!_sessions.TryAdd(sessionId, session))
            {
                throw new InvalidOperationException(
                    "Impossible d'enregistrer la session.");
            }

            _ = MonitorSessionAsync(sessionId, session);

            return Task.FromResult(sessionId);
        }
        catch
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Le processus est déjà terminé.
                }
            }

            process.Dispose();
            throw;
        }
    }

    public async Task SendInputAsync(
        TerminalSessionId id,
        string input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new InvalidOperationException(
                "Session de terminal introuvable.");
        }

        await session.Gate.WaitAsync(ct);

        try
        {
            if (session.IsDisposed || session.Process.HasExited)
            {
                throw new InvalidOperationException(
                    "Le processus du terminal est déjà arrêté.");
            }

            await session.Process.StandardInput.WriteAsync(
                input.AsMemory(),
                ct);

            await session.Process.StandardInput.FlushAsync(ct);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task StopSessionAsync(
        TerminalSessionId id,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(id, out var session))
        {
            return;
        }

        await session.Gate.WaitAsync(ct);

        try
        {
            if (session.IsDisposed)
            {
                return;
            }

            if (!session.Process.HasExited)
            {
                try
                {
                    session.Process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Le processus est déjà terminé.
                }
            }

            await session.Process.WaitForExitAsync(ct);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task MonitorSessionAsync(
        TerminalSessionId sessionId,
        TerminalSession session)
    {
        try
        {
            var outputTask = ReadOutputAsync(
                sessionId,
                session.Process.StandardOutput);

            var errorTask = ReadOutputAsync(
                sessionId,
                session.Process.StandardError);

            var exitTask = session.Process.WaitForExitAsync();

            await Task.WhenAll(outputTask, errorTask, exitTask);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            await session.Gate.WaitAsync();

            try
            {
                _sessions.TryRemove(sessionId, out _);

                if (!session.IsDisposed)
                {
                    session.IsDisposed = true;
                    session.Process.Dispose();
                }
            }
            finally
            {
                session.Gate.Release();
            }
        }
    }

    private async Task ReadOutputAsync(
        TerminalSessionId sessionId,
        StreamReader reader)
    {
        char[] buffer = new char[1024];

        while (true)
        {
            int count = await reader.ReadAsync(
                buffer.AsMemory(),
                CancellationToken.None);

            if (count == 0)
            {
                break;
            }

            string data = new string(buffer, 0, count);

            var handlers = OutputReceived;

            if (handlers is null)
            {
                continue;
            }

            var args = new TerminalOutputEventArgs(sessionId, data);

            foreach (var callback in handlers.GetInvocationList())
            {
                if (callback is not EventHandler<TerminalOutputEventArgs> handler)
                {
                    continue;
                }

                try
                {
                    handler(this, args);
                }
                catch
                {
                    // Un abonné défaillant ne doit pas interrompre
                    // la lecture des sorties du terminal.
                }
            }
        }
    }
}
