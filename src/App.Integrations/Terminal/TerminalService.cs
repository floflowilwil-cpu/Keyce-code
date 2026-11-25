
using System.Collections.Concurrent;
using System.Text;
using App.Core.Contracts;
using App.Core.Models;
using MiniPty;

namespace App.Integrations.Terminal;

public sealed class TerminalService : ITerminalService
{
    private sealed class TerminalSession(PtySession pty)
    {
        public PtySession Pty { get; } = pty;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool IsDisposed { get; set; }
        public Task MonitorTask { get; set; } = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<TerminalSessionId, TerminalSession>
        _sessions = new();

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public event EventHandler<TerminalOutputBytesEventArgs>? OutputBytesReceived;

    public async Task<TerminalSessionId> StartSessionAsync(
        TerminalStartOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        string shell = options.Shell ??
            (OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash");

        Dictionary<string, string?>? environment = options.Environment?
            .ToDictionary(
                variable => variable.Key,
                variable => (string?)variable.Value,
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

        var startInfo = new PtyStartInfo
        {
            FileName = shell,
            WorkingDirectory = options.WorkingDirectory
                ?? Environment.CurrentDirectory,
            InheritEnvironment = true,
            Environment = environment,
            Size = new PtySize(120, 30)
        };

        PtySession pty = Pty.Start(startInfo);

        try
        {
            var sessionId = TerminalSessionId.New();
            var session = new TerminalSession(pty);

            if (!_sessions.TryAdd(sessionId, session))
                throw new InvalidOperationException(
                    "Impossible d'enregistrer la session de terminal.");

            session.MonitorTask = MonitorSessionAsync(sessionId, session);

            return sessionId;
        }
        catch
        {
            await pty.DisposeAsync();
            throw;
        }
    }

    public Task SendInputAsync(
        TerminalSessionId id,
        string input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        byte[] inputBytes = Encoding.UTF8.GetBytes(input);

        return SendInputBytesAsync(id, inputBytes, ct);
    }

    public async Task SendInputBytesAsync(
        TerminalSessionId id,
        ReadOnlyMemory<byte> input,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(id, out var session))
            throw new InvalidOperationException(
                "Session de terminal introuvable.");

        await session.Gate.WaitAsync(ct);

        try
        {
            if (!_sessions.TryGetValue(id, out var currentSession)
                || !ReferenceEquals(currentSession, session))
            {
                throw new InvalidOperationException(
                    "Session de terminal introuvable.");
            }

            if (session.IsDisposed)
                throw new InvalidOperationException(
                    "La session de terminal est déjà arrêtée.");

            await session.Pty.WriteInputAsync(input, ct);
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
            return;

        await session.Gate.WaitAsync(ct);

        try
        {
            if (!session.IsDisposed)
            {
                session.IsDisposed = true;
                await session.Pty.DisposeAsync();
            }
        }
        finally
        {
            session.Gate.Release();
        }

        // Attendre la fin du lecteur PTY : aucune sortie de l'ancienne session
        // ne doit arriver après que l'interface a réinitialisé l'écran.
        try
        {
            await session.MonitorTask.WaitAsync(ct);
        }
        catch (IOException)
        {
            // Fermeture du transport PTY pendant son arrêt.
        }
        catch (ObjectDisposedException)
        {
            // Le PTY a déjà été libéré.
        }
    }

    private async Task MonitorSessionAsync(
        TerminalSessionId sessionId,
        TerminalSession session)
    {
        try
        {
            await ReadOutputAsync(sessionId, session);
        }
        catch (IOException)
        {
            // La session PTY peut se fermer pendant la lecture.
        }
        catch (ObjectDisposedException)
        {
            // La session a été libérée pendant la lecture.
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
                    await session.Pty.DisposeAsync();
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
        TerminalSession session)
    {
        Decoder decoder = Encoding.UTF8.GetDecoder();

        await foreach (var chunk in session.Pty.ReadOutputAsync())
        {
            byte[] outputBytes = chunk.Data.ToArray();

            RaiseOutputBytesReceived(sessionId, outputBytes);

            int maxChars = Encoding.UTF8.GetMaxCharCount(
                outputBytes.Length);

            char[] charBuffer = new char[maxChars];

            int charCount = decoder.GetChars(
                outputBytes,
                charBuffer,
                flush: false);

            if (charCount > 0)
            {
                string output = new(charBuffer, 0, charCount);
                RaiseOutputReceived(sessionId, output);
            }
        }

        char[] finalBuffer = new char[4];

        int finalCharCount = decoder.GetChars(
            ReadOnlySpan<byte>.Empty,
            finalBuffer,
            flush: true);

        if (finalCharCount > 0)
        {
            string finalOutput = new(finalBuffer, 0, finalCharCount);
            RaiseOutputReceived(sessionId, finalOutput);
        }
    }

    private void RaiseOutputReceived(
        TerminalSessionId sessionId,
        string data)
    {
        var handlers = OutputReceived;

        if (handlers is null)
            return;

        var args = new TerminalOutputEventArgs(sessionId, data);

        foreach (var callback in handlers.GetInvocationList())
        {
            if (callback is not EventHandler<TerminalOutputEventArgs> handler)
                continue;

            try
            {
                handler(this, args);
            }
            catch
            {
                // Un abonné défaillant ne doit pas interrompre les autres.
            }
        }
    }

    private void RaiseOutputBytesReceived(
        TerminalSessionId sessionId,
        byte[] data)
    {
        var handlers = OutputBytesReceived;

        if (handlers is null)
            return;

        var args = new TerminalOutputBytesEventArgs(sessionId, data);

        foreach (var callback in handlers.GetInvocationList())
        {
            if (callback is not EventHandler<TerminalOutputBytesEventArgs> handler)
                continue;

            try
            {
                handler(this, args);
            }
            catch
            {
                // Un abonné défaillant ne doit pas interrompre les autres.
            }
        }
    }
}
