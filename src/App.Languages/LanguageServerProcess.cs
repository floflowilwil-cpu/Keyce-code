using System.Diagnostics;
using StreamJsonRpc;

namespace App.Languages;


public sealed class LanguageServerProcess : IDisposable
{
    private Process? _process;

    /// <summary>Canal JSON-RPC, disponible après Start().</summary>
    public JsonRpc? Rpc { get; private set; }

    /// <summary>Messages envoyés par le serveur sur stderr (utile pour le débogage).</summary>
    public event Action<string>? ErrorOutput;

    public void Start(string command, string arguments = "", object? localTarget = null)
    {
        if (_process is not null)
            throw new InvalidOperationException("Le serveur est déjà démarré.");

        var startInfo = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Impossible de lancer '{command}'.");

        // Il faut lire stderr, sinon le serveur peut se bloquer quand le tampon est plein.
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) ErrorOutput?.Invoke(e.Data);
        };
        _process.BeginErrorReadLine();

        var handler = new HeaderDelimitedMessageHandler(
            _process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream,
            new JsonMessageFormatter());

        Rpc = new JsonRpc(handler);
        if (localTarget is not null)
        Rpc.AddLocalRpcTarget(localTarget);
        Rpc.StartListening();
    }

    public void Dispose()
    {
        Rpc?.Dispose();

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* déjà arrêté */ }
        }

        _process?.Dispose();
        _process = null;
    }
}