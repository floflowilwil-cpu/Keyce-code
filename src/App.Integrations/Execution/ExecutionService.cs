
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using App.Core.Contracts;
using App.Core.Models;

namespace App.Integrations.Execution;

/// <summary>
/// Executes external programs independently
/// from the interactive terminal and debugger.
/// </summary>
public sealed class ExecutionService : IExecutionService
{
    private sealed class ExecutionSession(Process process)
    {
        public Process Process { get; } = process;

        public Task StandardOutputTask { get; set; } =
            Task.CompletedTask;

        public Task StandardErrorTask { get; set; } =
            Task.CompletedTask;

        public object CompletionSync { get; } = new();

        public Task<ExecutionResult>? CompletionTask { get; set; }

        public bool IsDisposed { get; set; }
    }

    private readonly ConcurrentDictionary<
        ExecutionSessionId,
        ExecutionSession> _sessions = new();

    static ExecutionService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public event EventHandler<ExecutionOutputEventArgs>? OutputReceived;

    public async Task<ExecutionSessionId> StartExecutionAsync(
        ExecutionStartOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(options.Executable))
        {
            throw new ArgumentException(
                "The executable path is required.",
                nameof(options));
        }

        Encoding outputEncoding;

        try
        {
            outputEncoding = Encoding.GetEncoding(options.OutputCodePage);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                $"Unsupported output code page: {options.OutputCodePage}.",
                nameof(options),
                exception);
        }

        // UTF-8 sans BOM : un BOM envoyé sur l'entrée standard polluerait la première saisie.
        Encoding inputEncoding = options.OutputCodePage == 65001
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            : outputEncoding;

        string workingDirectory =
            options.WorkingDirectory is null
                ? Environment.CurrentDirectory
                : Path.GetFullPath(options.WorkingDirectory);

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Working directory not found: {workingDirectory}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = inputEncoding,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
            CreateNoWindow = true
        };

        if (options.RawArguments is not null)
        {
            startInfo.Arguments = options.RawArguments;
        }
        else if (options.Arguments is not null)
        {
            foreach (string argument in options.Arguments)
            {
                if (argument is null)
                {
                    throw new ArgumentException(
                        "An argument cannot be null.",
                        nameof(options));
                }

                startInfo.ArgumentList.Add(argument);
            }
        }

        if (options.Environment is not null)
        {
            foreach (var variable in options.Environment)
            {
                if (string.IsNullOrWhiteSpace(variable.Key))
                {
                    throw new ArgumentException(
                        "An environment variable name is required.",
                        nameof(options));
                }

                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        var process = new Process
        {
            StartInfo = startInfo
        };

        ExecutionSession? session = null;
        ExecutionSessionId sessionId = default;
        bool registered = false;

        try
        {
            ct.ThrowIfCancellationRequested();

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Unable to start: {options.Executable}");
            }

            session = new ExecutionSession(process);
            sessionId = ExecutionSessionId.New();

            if (!_sessions.TryAdd(sessionId, session))
            {
                throw new InvalidOperationException(
                    "Unable to register the execution session.");
            }

            registered = true;

            // 65001 (UTF-8) : détection automatique. Un programme .NET sous Windows écrit dans la page
            // de codes OEM de la console (850, 437...), pas en UTF-8 : sans repli, les accents seraient perdus.
            bool autoDetect = options.OutputCodePage == 65001;
            Encoding fallbackEncoding = autoDetect ? GetOemEncoding() : outputEncoding;

            session.StandardOutputTask = ReadOutputAsync(
                sessionId,
                process.StandardOutput.BaseStream,
                autoDetect,
                fallbackEncoding,
                ExecutionOutputStream.StandardOutput);

            session.StandardErrorTask = ReadOutputAsync(
                sessionId,
                process.StandardError.BaseStream,
                autoDetect,
                fallbackEncoding,
                ExecutionOutputStream.StandardError);

            return sessionId;
        }
        catch
        {
            if (registered)
            {
                _sessions.TryRemove(sessionId, out _);
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException)
            {
                // The process has already exited or been disposed.
            }
            finally
            {
                if (session is not null)
                {
                    await Task.WhenAll(
                        session.StandardOutputTask,
                        session.StandardErrorTask);
                }

                process.Dispose();
            }

            throw;
        }
    }

    public async Task StopExecutionAsync(
        ExecutionSessionId id,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Stopping an already completed or unknown session is a no-op.
        // This makes repeated concurrent stop requests safe.
        if (!_sessions.TryGetValue(id, out var session))
            return;

        lock (session.CompletionSync)
        {
            if (!session.IsDisposed)
            {
                try
                {
                    if (!session.Process.HasExited)
                    {
                        session.Process.Kill(
                            entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process has already exited or been disposed.
                }
            }
        }

        await GetOrStartCompletionTask(id, session).WaitAsync(ct);
    }

    public async Task SendInputAsync(
        ExecutionSessionId id,
        string text,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ct.ThrowIfCancellationRequested();

        var session = GetSession(id);

        try
        {
            await session.Process.StandardInput.WriteAsync(text.AsMemory(), ct);
            await session.Process.StandardInput.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new InvalidOperationException(
                "Le programme n'accepte plus de saisie (il est terminé).",
                ex);
        }
    }

    public async Task<ExecutionResult> WaitForExitAsync(
        ExecutionSessionId id,
        CancellationToken ct = default)
    {
        var session = GetSession(id);

        return await GetOrStartCompletionTask(id, session)
            .WaitAsync(ct);
    }

    private Task<ExecutionResult> GetOrStartCompletionTask(
        ExecutionSessionId id,
        ExecutionSession session)
    {
        lock (session.CompletionSync)
        {
            return session.CompletionTask ??=
                CompleteSessionCoreAsync(id, session);
        }
    }

    private async Task<ExecutionResult> CompleteSessionCoreAsync(
        ExecutionSessionId id,
        ExecutionSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync();

            await Task.WhenAll(
                session.StandardOutputTask,
                session.StandardErrorTask);

            lock (session.CompletionSync)
            {
                int exitCode = session.Process.ExitCode;

                return new ExecutionResult(id, exitCode);
            }
        }
        finally
        {
            lock (session.CompletionSync)
            {
                session.IsDisposed = true;

                _sessions.TryRemove(id, out _);

                session.Process.Dispose();
            }
        }
    }

    private async Task ReadOutputAsync(
        ExecutionSessionId sessionId,
        Stream stream,
        bool autoDetect,
        Encoding fallback,
        ExecutionOutputStream kind)
    {
        byte[] buffer = new byte[4096];
        byte[] carry = Array.Empty<byte>();
        var strictUtf8 = new UTF8Encoding(false, true);

        try
        {
            while (true)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));

                if (count == 0)
                    break;

                byte[] data = new byte[carry.Length + count];
                Buffer.BlockCopy(carry, 0, data, 0, carry.Length);
                Buffer.BlockCopy(buffer, 0, data, carry.Length, count);

                // Un caractère UTF-8 peut être coupé entre deux lectures : on garde sa fin en attente.
                int keep = autoDetect ? IncompleteUtf8Tail(data) : 0;
                int usable = data.Length - keep;

                carry = keep == 0
                    ? Array.Empty<byte>()
                    : data.AsSpan(usable).ToArray();

                if (usable == 0)
                    continue;

                RaiseOutputReceived(
                    sessionId,
                    kind,
                    Decode(data, usable, autoDetect ? strictUtf8 : null, fallback));
            }

            if (carry.Length > 0)
            {
                RaiseOutputReceived(
                    sessionId,
                    kind,
                    Decode(carry, carry.Length, autoDetect ? strictUtf8 : null, fallback));
            }
        }
        catch (IOException)
        {
            // The stream was closed while the process was stopping.
        }
        catch (ObjectDisposedException)
        {
            // The stream has already been disposed.
        }
    }

    private static string Decode(
        byte[] data,
        int count,
        UTF8Encoding? strictUtf8,
        Encoding fallback)
    {
        if (strictUtf8 is not null)
        {
            try
            {
                return strictUtf8.GetString(data, 0, count);
            }
            catch (DecoderFallbackException)
            {
                // Pas de l'UTF-8 valide : page de codes de la console.
            }
        }

        return fallback.GetString(data, 0, count);
    }

    /// <summary>Nombre d'octets en fin de tampon qui forment le début d'un caractère UTF-8 incomplet.</summary>
    private static int IncompleteUtf8Tail(byte[] data)
    {
        int max = Math.Min(3, data.Length);

        for (int back = 1; back <= max; back++)
        {
            byte value = data[data.Length - back];

            if ((value & 0xC0) == 0x80)
                continue;

            int expected = value >= 0xF0 ? 4 : value >= 0xE0 ? 3 : value >= 0xC0 ? 2 : 1;

            return expected > back ? back : 0;
        }

        return 0;
    }

    private static Encoding GetOemEncoding()
    {
        try
        {
            return Encoding.GetEncoding(
                System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }

    private void RaiseOutputReceived(
        ExecutionSessionId sessionId,
        ExecutionOutputStream stream,
        string data)
    {
        var handlers = OutputReceived;

        if (handlers is null)
            return;

        var args = new ExecutionOutputEventArgs(
            sessionId,
            stream,
            data);

        foreach (var callback in handlers.GetInvocationList())
        {
            if (callback is not EventHandler<ExecutionOutputEventArgs> handler)
                continue;

            try
            {
                handler(this, args);
            }
            catch
            {
                // A failing subscriber must not interrupt output reading.
            }
        }
    }

    private ExecutionSession GetSession(ExecutionSessionId id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new InvalidOperationException(
                "Execution session not found.");
        }

        return session;
    }
}
