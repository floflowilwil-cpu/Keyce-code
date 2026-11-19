
using System.Collections.Concurrent;
using System.Diagnostics;
using App.Core.Contracts;
using App.Core.Models;

namespace App.Integrations.Terminal;

public sealed class TerminalService : ITerminalService
{
    private readonly ConcurrentDictionary<TerminalSessionId, Process> _sessions = new();

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    // Démarre une nouvelle session de terminal
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

        // Ajout des variables d'environnement
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

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Impossible de démarrer le terminal.");
            }

            var sessionId = TerminalSessionId.New();

            if (!_sessions.TryAdd(sessionId, process))
            {
                throw new InvalidOperationException(
                    "Impossible d'enregistrer la session.");
            }

            // Lecture des sorties en arrière-plan
            _ = MonitorSessionAsync(sessionId, process);

            return Task.FromResult(sessionId);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    // Envoie une commande au terminal
    public async Task SendInputAsync(
        TerminalSessionId id,
        string input,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(id, out var process))
        {
            throw new InvalidOperationException(
                "Session de terminal introuvable.");
        }

        if (process.HasExited)
        {
            throw new InvalidOperationException(
                "Le processus du terminal est déjà arrêté.");
        }

        await process.StandardInput.WriteLineAsync(input.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    // Surveille les sorties standard et les erreurs
    private async Task MonitorSessionAsync(
        TerminalSessionId sessionId,
        Process process)
    {
        try
        {
            var outputTask = ReadOutputAsync(
                sessionId,
                process.StandardOutput);

            var errorTask = ReadOutputAsync(
                sessionId,
                process.StandardError);

            await Task.WhenAll(outputTask, errorTask);
        }
        catch (IOException)
        {
            // Le processus peut fermer ses flux brutalement.
        }
        catch (ObjectDisposedException)
        {
            // Le processus a déjà été libéré.
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            process.Dispose();
        }
    }

    // Lit les données progressivement pour permettre
    // leur transmission en temps réel à l'interface.
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
                break;

            string data = new string(buffer, 0, count);

            OutputReceived?.Invoke(
                this,
                new TerminalOutputEventArgs(sessionId, data));
        }
    }
}
