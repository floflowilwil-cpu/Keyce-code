
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

        if (options.Arguments is not null)
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

            session.StandardOutputTask = ReadOutputAsync(
                sessionId,
                process.StandardOutput,
                ExecutionOutputStream.StandardOutput);

            session.StandardErrorTask = ReadOutputAsync(
                sessionId,
                process.StandardError,
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
        StreamReader reader,
        ExecutionOutputStream stream)
    {
        char[] buffer = new char[4096];

        try
        {
            while (true)
            {
                int count = await reader.ReadAsync(
                    buffer.AsMemory(0, buffer.Length));

                if (count == 0)
                    break;

                string data = new(buffer, 0, count);

                RaiseOutputReceived(
                    sessionId,
                    stream,
                    data);
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
