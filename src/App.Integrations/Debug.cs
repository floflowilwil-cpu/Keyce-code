
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using App.Core.Contracts;
using App.Core.Models;

namespace App.Integrations.Debug;

public sealed class DebugService : IDebugService
{
    private sealed class DebugSession(
        Process process,
        DebugAdapterDefinition adapter) : IDisposable
    {
        public Process Process { get; } = process;

        public DebugAdapterDefinition Adapter { get; } = adapter;

        /// <summary>Flux d'écriture des requêtes DAP (stdin du processus ou socket TCP).</summary>
        public Stream? Input { get; set; }

        /// <summary>Flux de lecture des messages DAP (stdout du processus ou socket TCP).</summary>
        public Stream? Output { get; set; }

        public TcpClient? Connection { get; set; }

        public Task? StdoutDrainTask { get; set; }

        public object BreakpointLock { get; } = new();

        /// <summary>Lignes des points d'arrêt par fichier : DAP remplace la liste du fichier à chaque envoi.</summary>
        public Dictionary<string, List<int>> Breakpoints { get; } = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        public void Dispose()
        {
            try
            {
                Connection?.Dispose();
            }
            catch (Exception)
            {
                // Connexion déjà fermée.
            }

            Process.Dispose();
        }

        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        public SemaphoreSlim StopGate { get; } = new(1, 1);

        public ConcurrentDictionary<int, TaskCompletionSource<JsonObject>>
            PendingRequests { get; } = new();

        public TaskCompletionSource<bool> Initialized { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<int> InitialStopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<DebugStopInfo> StopEvents { get; } =
            Channel.CreateUnbounded<DebugStopInfo>();

        public Task? ReaderTask { get; set; }

        public Task<string>? ErrorReaderTask { get; set; }

        public int LastStoppedThreadId { get; set; }

        public int NextSequence;
    }

    private readonly ConcurrentDictionary<DebugSessionId, DebugSession>
        _sessions = new();

    private readonly DebugAdapterRegistry _registry;

    public DebugService() : this(DebugAdapterRegistry.CreateDefault())
    {
    }

    /// <param name="registry">Adaptateurs de débogage disponibles (un par langage).</param>
    public DebugService(DebugAdapterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public async Task<DebugSessionId> StartDebugAsync(
        DebugStartOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(options.Program))
            throw new ArgumentException(
                "Le chemin du programme est obligatoire.",
                nameof(options));

        string program = Path.GetFullPath(options.Program);

        if (!File.Exists(program))
            throw new FileNotFoundException(
                "Le programme à déboguer est introuvable.",
                program);

        DebugAdapterDefinition adapter =
            _registry.Resolve(options.LanguageId, program);

        adapter.ValidateProgram?.Invoke(program);

        string workingDirectory = Path.GetFullPath(
            options.WorkingDirectory
            ?? Path.GetDirectoryName(program)!);

        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(
                $"Répertoire introuvable : {workingDirectory}");

        int port = adapter.Transport == DebugTransport.Tcp
            ? GetFreeTcpPort()
            : 0;

        DebugAdapterCommand command =
            adapter.CreateCommand(port)
            ?? throw new FileNotFoundException(adapter.MissingMessage);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process
        {
            StartInfo = startInfo
        };

        DebugSession? session = null;
        DebugSessionId sessionId = default;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException(
                    $"Impossible de démarrer {adapter.DebuggerName}.");

            session = new DebugSession(process, adapter);
            sessionId = DebugSessionId.New();

            if (!_sessions.TryAdd(sessionId, session))
                throw new InvalidOperationException(
                    "Impossible d'enregistrer la session de débogage.");

            session.ErrorReaderTask =
                process.StandardError.ReadToEndAsync();

            _ = MonitorProcessAsync(sessionId, session);

            if (adapter.Transport == DebugTransport.Tcp)
            {
                session.Connection =
                    await ConnectAsync(process, port, adapter, ct);

                NetworkStream stream = session.Connection.GetStream();

                session.Input = stream;
                session.Output = stream;

                // La sortie standard n'est pas du DAP : on la vide pour ne pas bloquer le processus.
                session.StdoutDrainTask =
                    process.StandardOutput.ReadToEndAsync();
            }
            else
            {
                session.Input = process.StandardInput.BaseStream;
                session.Output = process.StandardOutput.BaseStream;
            }

            session.ReaderTask = ReadMessagesAsync(session);

            var initializeArguments = new JsonObject
            {
                ["clientID"] = "Keyce-code",
                ["clientName"] = "Keyce Code",
                ["adapterID"] = adapter.AdapterId,
                ["pathFormat"] = "path",
                ["linesStartAt1"] = true,
                ["columnsStartAt1"] = true,
                ["supportsRunInTerminalRequest"] = false
            };

            await SendRequestAsync(
                session,
                "initialize",
                initializeArguments,
                ct);

            JsonObject launchArguments = adapter.CreateLaunchArguments(
                new DebugLaunchContext(
                    program,
                    options.Arguments ?? Array.Empty<string>(),
                    workingDirectory,
                    options.Environment));

            Task<JsonObject> launchTask = SendRequestAsync(
                session,
                "launch",
                launchArguments,
                ct,
                adapter.StartupTimeout);

            // Si le lancement échoue (compilation, programme invalide…), on remonte son erreur
            // au lieu d'attendre l'événement « initialized » qui n'arrivera jamais.
            Task first = await Task.WhenAny(
                    session.Initialized.Task,
                    launchTask)
                .WaitAsync(adapter.StartupTimeout, ct);

            if (first == launchTask)
                await launchTask;

            await session.Initialized.Task.WaitAsync(
                adapter.StartupTimeout,
                ct);

            await SendRequestAsync(
                session,
                "configurationDone",
                new JsonObject(),
                ct);

            await launchTask;

            await WaitForInitialStopAsync(session, ct);

            // L'arrêt initial correspond à stopAtEntry.
            await session.StopEvents.Reader.ReadAsync(ct)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), ct);

            return sessionId;
        }
        catch (Exception exception)
        {
            string? details = null;

            if (session is not null)
            {
                details = await ReadAdapterErrorAsync(session);

                try
                {
                    await ForceStopProcessAsync(session);
                }
                finally
                {
                    _sessions.TryRemove(sessionId, out _);
                    session.Dispose();
                }
            }
            else
            {
                process.Dispose();
            }

            if (details is not null
                && exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    exception.Message + Environment.NewLine + details,
                    exception);
            }

            throw;
        }
    }

    /// <summary>
    /// Si l'adaptateur s'est arrêté de lui-même (outil mal installé, erreur de compilation…),
    /// retourne sa sortie d'erreur et un conseil d'installation ; sinon null.
    /// </summary>
    private static async Task<string?> ReadAdapterErrorAsync(
        DebugSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(1));

            if (session.Process.ExitCode == 0
                || session.ErrorReaderTask is null)
            {
                return null;
            }

            string error = (await session.ErrorReaderTask
                .WaitAsync(TimeSpan.FromSeconds(1))).Trim();

            if (error.Length > 800)
                error = error[..800] + "…";

            string hint = session.Adapter.InstallHint;

            return error.Length == 0
                ? hint
                : error + Environment.NewLine + hint;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<TcpClient> ConnectAsync(
        Process process,
        int port,
        DebugAdapterDefinition adapter,
        CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
                throw new InvalidOperationException(
                    $"{adapter.DebuggerName} s'est arrêté avant d'accepter la connexion.");

            var client = new TcpClient();

            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();

                if (clock.Elapsed >= adapter.StartupTimeout)
                    throw new TimeoutException(
                        $"Impossible de se connecter à {adapter.DebuggerName} (port {port}).");

                await Task.Delay(100, ct);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private static async Task WaitForInitialStopAsync(
        DebugSession session,
        CancellationToken ct)
    {
        Task completed = await Task.WhenAny(
                session.InitialStopped.Task,
                session.Ended.Task)
            .WaitAsync(session.Adapter.StartupTimeout, ct);

        if (session.Ended.Task.IsCompleted
            && !session.InitialStopped.Task.IsCompleted)
        {
            throw new InvalidOperationException(
                $"{session.Adapter.DebuggerName} a terminé la session avant le premier arrêt.");
        }

        await completed.WaitAsync(ct);

        await session.InitialStopped.Task.WaitAsync(ct);
    }

    public async Task SetBreakpointAsync(
        DebugSessionId id,
        Breakpoint breakpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(breakpoint);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(breakpoint.Path))
            throw new ArgumentException(
                "Le chemin du fichier est obligatoire.",
                nameof(breakpoint));

        if (breakpoint.Line < 1)
            throw new ArgumentOutOfRangeException(
                nameof(breakpoint),
                "La ligne doit être supérieure ou égale à 1.");

        var session = GetSession(id);

        EnsureSessionRunning(session);

        string fullPath = Path.GetFullPath(breakpoint.Path);
        int[] lines;
        bool added = false;

        lock (session.BreakpointLock)
        {
            if (!session.Breakpoints.TryGetValue(
                fullPath,
                out List<int>? known))
            {
                known = new List<int>();
                session.Breakpoints[fullPath] = known;
            }

            if (!known.Contains(breakpoint.Line))
            {
                known.Add(breakpoint.Line);
                added = true;
            }

            lines = known.ToArray();
        }

        // DAP remplace tous les points d'arrêt du fichier : on envoie la liste complète.
        var arguments = new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["path"] = fullPath
            },
            ["breakpoints"] = new JsonArray(
                lines
                    .Select(line => (JsonNode?)new JsonObject
                    {
                        ["line"] = line
                    })
                    .ToArray()),
            ["sourceModified"] = false
        };

        bool accepted = false;

        try
        {
            JsonObject response = await SendRequestAsync(
                session,
                "setBreakpoints",
                arguments,
                ct);

            JsonArray? breakpoints =
                response["body"]?["breakpoints"] as JsonArray;

            if (breakpoints is null || breakpoints.Count == 0)
                throw new InvalidOperationException(
                    $"{session.Adapter.DebuggerName} n'a retourné aucun breakpoint.");

            int index = Array.IndexOf(lines, breakpoint.Line);

            bool verified = index >= 0
                && index < breakpoints.Count
                && (breakpoints[index]?["verified"]?.GetValue<bool>() ?? false);

            if (!verified)
                throw new InvalidOperationException(
                    $"{session.Adapter.DebuggerName} n'a pas validé le breakpoint.");

            accepted = true;
        }
        finally
        {
            if (!accepted && added)
            {
                lock (session.BreakpointLock)
                {
                    if (session.Breakpoints.TryGetValue(
                        fullPath,
                        out List<int>? known))
                    {
                        known.Remove(breakpoint.Line);
                    }
                }
            }
        }
    }

    public async Task StepAsync(
        DebugSessionId id,
        StepKind kind,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var session = GetSession(id);

        EnsureSessionRunning(session);

        int threadId = session.LastStoppedThreadId;

        if (threadId <= 0)
            throw new InvalidOperationException(
                "Aucun thread arrêté disponible pour le débogage.");

        string command = kind switch
        {
            StepKind.Over => "next",
            StepKind.Into => "stepIn",
            StepKind.Out => "stepOut",
            StepKind.Continue => "continue",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var arguments = new JsonObject
        {
            ["threadId"] = threadId
        };

        if (kind != StepKind.Continue)
            arguments["singleThread"] = false;

        await SendRequestAsync(
            session,
            command,
            arguments,
            ct);
    }

    public async Task<DebugStopInfo> WaitForStopAsync(
        DebugSessionId id,
        CancellationToken ct = default)
    {
        var session = GetSession(id);

        DebugStopInfo stop;

        try
        {
            stop = await session.StopEvents.Reader
                .ReadAsync(ct)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException(
                "La session de débogage est terminée. Aucun nouvel arrêt n'est disponible.");
        }

        if (session.Process.HasExited || session.Ended.Task.IsCompleted)
            throw new InvalidOperationException(
                "La session s'est terminée avant la récupération de la pile d'appels.");

        var response = await SendRequestAsync(
            session,
            "stackTrace",
            new JsonObject
            {
                ["threadId"] = stop.ThreadId,
                ["startFrame"] = 0,
                ["levels"] = 1
            },
            ct);

        JsonObject? frame =
            (response["body"]?["stackFrames"] as JsonArray)?
            .FirstOrDefault() as JsonObject;

        int? line = frame?["line"]?.GetValue<int>();

        string? sourcePath =
            frame?["source"]?["path"]?.GetValue<string>();

        return stop with
        {
            Line = line,
            SourcePath = sourcePath
        };
    }

    public async Task StopDebugAsync(
        DebugSessionId id,
        CancellationToken ct = default)
    {
        var session = GetSession(id);

        await session.StopGate.WaitAsync(ct);

        try
        {
            bool cancellationRequested = false;

            if (!session.Process.HasExited)
            {
                try
                {
                    await SendRequestAsync(
                        session,
                        "disconnect",
                        new JsonObject
                        {
                            ["restart"] = false,
                            ["terminateDebuggee"] = true
                        },
                        ct).WaitAsync(
                            TimeSpan.FromSeconds(5),
                            ct);
                }
                catch (OperationCanceledException)
                {
                    cancellationRequested = true;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                    or IOException
                    or TimeoutException
                    or EndOfStreamException
                    or ObjectDisposedException)
                {
                    // On forcera l'arrêt si la fermeture DAP échoue.
                }
            }

            if (!session.Exited.Task.IsCompleted)
            {
                try
                {
                    await session.Exited.Task.WaitAsync(
                        TimeSpan.FromSeconds(5),
                        cancellationRequested
                            ? CancellationToken.None
                            : ct);
                }
                catch (OperationCanceledException)
                {
                    cancellationRequested = true;
                }
                catch (TimeoutException)
                {
                    // Le processus sera arrêté manuellement.
                }
            }

            if (!session.Exited.Task.IsCompleted)
                await ForceStopProcessAsync(session);

            await session.Exited.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            _sessions.TryRemove(id, out _);

            if (session.ErrorReaderTask is not null)
            {
                try
                {
                    await session.ErrorReaderTask.WaitAsync(
                        TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    // La sortie d'erreur n'a pas été fermée à temps.
                }
            }

            session.Dispose();

            if (cancellationRequested || ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
        }
        finally
        {
            session.StopGate.Release();
        }
    }

    private async Task ForceStopProcessAsync(DebugSession session)
    {
        try
        {
            if (!session.Process.HasExited)
                session.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Processus déjà terminé.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Processus terminé ou inaccessible.
        }

        await session.Process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        await session.Exited.Task.WaitAsync(
            TimeSpan.FromSeconds(10));
    }

    private DebugSession GetSession(DebugSessionId id)
    {
        if (!_sessions.TryGetValue(id, out var session))
            throw new InvalidOperationException(
                "Session de débogage introuvable.");

        return session;
    }

    private static void EnsureSessionRunning(DebugSession session)
    {
        if (session.Process.HasExited
            || session.Ended.Task.IsCompleted)
        {
            throw new InvalidOperationException(
                "La session de débogage est terminée.");
        }
    }

    private async Task<JsonObject> SendRequestAsync(
        DebugSession session,
        string command,
        JsonObject arguments,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();

        if (session.Process.HasExited)
            throw new InvalidOperationException(
                $"Le processus {session.Adapter.DebuggerName} est déjà terminé.");

        int sequence = Interlocked.Increment(
            ref session.NextSequence);

        var completion = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!session.PendingRequests.TryAdd(sequence, completion))
            throw new InvalidOperationException(
                "Impossible d'enregistrer la requête DAP.");

        try
        {
            var request = new JsonObject
            {
                ["seq"] = sequence,
                ["type"] = "request",
                ["command"] = command,
                ["arguments"] = arguments
            };

            byte[] body = Encoding.UTF8.GetBytes(
                request.ToJsonString());

            byte[] header = Encoding.ASCII.GetBytes(
                $"Content-Length: {body.Length}\r\n\r\n");

            await session.WriteGate.WaitAsync(ct);

            try
            {
                Stream stream = session.Input
                    ?? throw new InvalidOperationException(
                        "Le canal de communication DAP n'est pas ouvert.");

                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(body, ct);
                await stream.FlushAsync(ct);
            }
            finally
            {
                session.WriteGate.Release();
            }

            JsonObject response = await completion.Task.WaitAsync(
                timeout ?? TimeSpan.FromSeconds(30),
                ct);

            if (response["success"]?.GetValue<bool>() == false)
            {
                string message =
                    response["message"]?.GetValue<string>()
                    ?? $"La requête DAP '{command}' a échoué.";

                throw new InvalidOperationException(message);
            }

            return response;
        }
        finally
        {
            session.PendingRequests.TryRemove(sequence, out _);
        }
    }

    private async Task ReadMessagesAsync(DebugSession session)
    {
        try
        {
            Stream stream = session.Output
                ?? throw new InvalidOperationException(
                    "Le canal de communication DAP n'est pas ouvert.");

            while (true)
            {
                byte[]? body = await ReadMessageAsync(stream);

                if (body is null)
                    break;

                var message =
                    JsonNode.Parse(body) as JsonObject;

                if (message is null)
                    continue;

                string? type =
                    message["type"]?.GetValue<string>();

                if (type == "response")
                {
                    int requestSequence =
                        message["request_seq"]?.GetValue<int>() ?? -1;

                    if (session.PendingRequests.TryGetValue(
                        requestSequence,
                        out var completion))
                    {
                        completion.TrySetResult(message);
                    }
                }
                else if (type == "event")
                {
                    HandleEvent(session, message);
                }
            }
        }
        catch (Exception exception)
        {
            foreach (var pending in session.PendingRequests.Values)
                pending.TrySetException(exception);

            session.Initialized.TrySetException(exception);
            session.InitialStopped.TrySetException(exception);
        }
        finally
        {
            session.Ended.TrySetResult(true);
            session.StopEvents.Writer.TryComplete();
        }
    }

    private static void HandleEvent(
        DebugSession session,
        JsonObject message)
    {
        string? eventName =
            message["event"]?.GetValue<string>();

        switch (eventName)
        {
            case "initialized":
                session.Initialized.TrySetResult(true);
                break;

            case "stopped":
            {
                JsonObject? body =
                    message["body"] as JsonObject;

                int threadId =
                    body?["threadId"]?.GetValue<int>() ?? 0;

                if (threadId <= 0)
                    break;

                string reason =
                    body?["reason"]?.GetValue<string>() ?? "unknown";

                session.LastStoppedThreadId = threadId;

                session.StopEvents.Writer.TryWrite(
                    new DebugStopInfo(
                        reason,
                        threadId,
                        null,
                        null));

                session.InitialStopped.TrySetResult(threadId);
                break;
            }

            case "exited":
            case "terminated":
                session.Ended.TrySetResult(true);
                session.StopEvents.Writer.TryComplete();
                break;
        }
    }

    private static async Task<byte[]?> ReadMessageAsync(
        Stream stream)
    {
        using var headerStream = new MemoryStream();

        byte[] singleByte = new byte[1];

        while (true)
        {
            int count = await stream.ReadAsync(
                singleByte.AsMemory(),
                CancellationToken.None);

            if (count == 0)
            {
                if (headerStream.Length == 0)
                    return null;

                throw new EndOfStreamException(
                    "En-tête DAP incomplet.");
            }

            headerStream.WriteByte(singleByte[0]);

            if (headerStream.Length > 8192)
                throw new InvalidDataException(
                    "En-tête DAP trop volumineux.");

            byte[] headerBytes = headerStream.ToArray();
            int length = headerBytes.Length;

            if (length >= 4
                && headerBytes[length - 4] == '\r'
                && headerBytes[length - 3] == '\n'
                && headerBytes[length - 2] == '\r'
                && headerBytes[length - 1] == '\n')
            {
                break;
            }
        }

        string header = Encoding.ASCII.GetString(
            headerStream.ToArray());

        int contentLength = -1;

        foreach (string line in header.Split(
            "\r\n",
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(
                "Content-Length:",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value =
                line["Content-Length:".Length..].Trim();

            if (!int.TryParse(value, out contentLength))
                throw new InvalidDataException(
                    "Content-Length DAP invalide.");

            break;
        }

        if (contentLength < 0 || contentLength > 50_000_000)
            throw new InvalidDataException(
                "Longueur du message DAP invalide.");

        byte[] body = new byte[contentLength];
        int offset = 0;

        while (offset < contentLength)
        {
            int count = await stream.ReadAsync(
                body.AsMemory(offset, contentLength - offset),
                CancellationToken.None);

            if (count == 0)
                throw new EndOfStreamException(
                    "Corps du message DAP incomplet.");

            offset += count;
        }

        return body;
    }

    private async Task MonitorProcessAsync(
        DebugSessionId id,
        DebugSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync();

            if (session.ReaderTask is not null)
            {
                try
                {
                    await session.ReaderTask.WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Le flux peut se fermer normalement avec le processus.
                }
            }

            foreach (var pending in session.PendingRequests.Values)
            {
                pending.TrySetException(
                    new EndOfStreamException(
                        $"{session.Adapter.DebuggerName} a terminé son exécution."));
            }
        }
        catch (Exception exception)
        {
            foreach (var pending in session.PendingRequests.Values)
                pending.TrySetException(exception);
        }
        finally
        {
            session.Ended.TrySetResult(true);
            session.StopEvents.Writer.TryComplete();
            session.Exited.TrySetResult(true);
        }

        // La session reste dans le dictionnaire jusqu'à StopDebugAsync.
    }
}
