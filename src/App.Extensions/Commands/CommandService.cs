using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Extensions.Commands;

/// <summary>Implémentation en mémoire du registre de commandes.</summary>
public sealed class CommandService : ICommandService
{
    private readonly Dictionary<string, Entry> _commands = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public event EventHandler? CommandsChanged;

    public IReadOnlyList<CommandInfo> Commands
    {
        get
        {
            lock (_lock)
            {
                return _commands.Values
                    .Select(e => e.Info)
                    .OrderBy(c => c.Id, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }

    public IDisposable Register(
        string commandId,
        string title,
        Func<CancellationToken, Task> handler,
        string? ownerId = null)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            throw new AppException(
                ErrorCode.InvalidArgument, "L'identifiant de la commande est obligatoire.");

        if (handler is null)
            throw new AppException(
                ErrorCode.InvalidArgument, "Le gestionnaire de la commande est obligatoire.");

        var entry = new Entry(
            new CommandInfo(
                commandId,
                string.IsNullOrWhiteSpace(title) ? commandId : title,
                ownerId),
            handler);

        lock (_lock)
        {
            if (!_commands.TryAdd(commandId, entry))
                throw new AppException(
                    ErrorCode.AlreadyExists,
                    $"La commande '{commandId}' est déjà enregistrée.");
        }

        CommandsChanged?.Invoke(this, EventArgs.Empty);
        return new Registration(this, entry);
    }

    public async Task ExecuteAsync(string commandId, CancellationToken ct = default)
    {
        Entry? entry;
        lock (_lock)
            _commands.TryGetValue(commandId ?? string.Empty, out entry);

        if (entry is null)
            throw new AppException(
                ErrorCode.NotFound, $"Commande introuvable : {commandId}");

        ct.ThrowIfCancellationRequested();

        try
        {
            await entry.Handler(ct);
        }
        catch (AppException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Une commande (souvent venant d'une extension) ne doit pas faire planter l'application.
            throw new AppException(
                ErrorCode.Unknown,
                $"La commande '{commandId}' a échoué : {ex.Message}",
                ex);
        }
    }

    private void Unregister(Entry entry)
    {
        bool removed;
        lock (_lock)
        {
            removed = _commands.TryGetValue(entry.Info.Id, out Entry? current)
                && ReferenceEquals(current, entry)
                && _commands.Remove(entry.Info.Id);
        }

        if (removed)
            CommandsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Entry(CommandInfo Info, Func<CancellationToken, Task> Handler);

    private sealed class Registration(CommandService owner, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unregister(entry);
        }
    }
}
