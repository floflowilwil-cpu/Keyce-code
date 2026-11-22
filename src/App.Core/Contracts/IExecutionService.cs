using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>
/// Exécution de programmes externes.
/// Utilisé par l'interface et les tâches.
/// </summary>
public interface IExecutionService
{
    /// <summary>
    /// Démarre un programme et retourne son identifiant de session.
    /// </summary>
    Task<ExecutionSessionId> StartExecutionAsync(
        ExecutionStartOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Arrête une session d'exécution.
    /// </summary>
    Task StopExecutionAsync(
        ExecutionSessionId id,
        CancellationToken ct = default);

    /// <summary>
    /// Attend la fin d'une session et retourne son résultat.
    /// </summary>
    Task<ExecutionResult> WaitForExitAsync(
        ExecutionSessionId id,
        CancellationToken ct = default);

    /// <summary>
    /// Envoie du texte sur l'entrée standard d'un programme en cours d'exécution
    /// (par exemple pour répondre à Console.ReadLine).
    /// </summary>
    Task SendInputAsync(
        ExecutionSessionId id,
        string text,
        CancellationToken ct = default);

    /// <summary>
    /// Se déclenche lorsqu'un processus produit des données.
    /// </summary>
    event EventHandler<ExecutionOutputEventArgs>? OutputReceived;
}