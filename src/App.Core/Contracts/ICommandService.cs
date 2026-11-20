using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Registre des commandes. Utilisé par UI (palette) et extensions.</summary>
public interface ICommandService
{
    IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>Enregistre une commande. Disposer le résultat la retire.</summary>
    IDisposable Register(
        string commandId,
        string title,
        Func<CancellationToken, Task> handler,
        string? ownerId = null);

    Task ExecuteAsync(string commandId, CancellationToken ct = default);

    event EventHandler? CommandsChanged;
}
