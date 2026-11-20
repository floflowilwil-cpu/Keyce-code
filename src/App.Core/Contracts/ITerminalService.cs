
using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>
/// Terminal intégré (processus / pseudo-terminal).
/// Utilisé par l'interface et les tâches.
/// </summary>
public interface ITerminalService
{
    Task<TerminalSessionId> StartSessionAsync(
        TerminalStartOptions options,
        CancellationToken ct = default);

    Task SendInputAsync(
        TerminalSessionId id,
        string input,
        CancellationToken ct = default);

    Task StopSessionAsync(
        TerminalSessionId id,
        CancellationToken ct = default);

    event EventHandler<TerminalOutputEventArgs> OutputReceived;
}
