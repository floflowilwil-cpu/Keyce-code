
using System.Collections.Generic;

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
/// <c>RawArguments</c> : ligne d'arguments transmise telle quelle (sans échappement), utile pour « cmd.exe /c ».
/// Elle prend le pas sur <c>Arguments</c>.
/// </summary>
public sealed record ExecutionStartOptions(
    string Executable,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    int OutputCodePage = 65001,
    string? RawArguments = null);

/// <summary>
/// Résultat final d'une exécution.
/// </summary>
public sealed record ExecutionResult(
    ExecutionSessionId SessionId,
    int ExitCode);
