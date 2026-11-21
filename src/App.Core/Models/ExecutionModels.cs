namespace App.Core.Models;

/// <summary>
/// Identifiant unique d'une session d'exécution.
/// </summary>
public readonly record struct ExecutionSessionId(Guid Value)
{
    public static ExecutionSessionId New() => new(Guid.NewGuid());
}

/// <summary>
/// Paramètres nécessaires au lancement d'un programme.
/// </summary>
public sealed record ExecutionStartOptions(
    string Executable,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// Résultat final d'une exécution.
/// </summary>
public sealed record ExecutionResult(
    ExecutionSessionId SessionId,
    int ExitCode);