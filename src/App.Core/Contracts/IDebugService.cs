
using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Débogage via DAP. Utilisé par UI et tâches.</summary>
public interface IDebugService
{
    Task<DebugSessionId> StartDebugAsync(
        DebugStartOptions options,
        CancellationToken ct = default);

    Task SetBreakpointAsync(
        DebugSessionId id,
        Breakpoint breakpoint,
        CancellationToken ct = default);

    Task StepAsync(
        DebugSessionId id,
        StepKind kind,
        CancellationToken ct = default);

    Task<DebugStopInfo> WaitForStopAsync(
        DebugSessionId id,
        CancellationToken ct = default);

    Task StopDebugAsync(
        DebugSessionId id,
        CancellationToken ct = default);

    /// <summary>
    /// Se déclenche lorsque le programme débogué écrit sur sa sortie standard ou d'erreur.
    /// </summary>
    event EventHandler<DebugOutputEventArgs>? OutputReceived;
}
