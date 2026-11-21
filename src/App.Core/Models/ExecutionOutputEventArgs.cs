namespace App.Core.Models;

/// <summary>
/// Données reçues depuis un flux de sortie d'un processus.
/// </summary>
public sealed class ExecutionOutputEventArgs(
    ExecutionSessionId sessionId,
    ExecutionOutputStream stream,
    string data) : EventArgs
{
    public ExecutionSessionId SessionId { get; } = sessionId;

    public ExecutionOutputStream Stream { get; } = stream;

    public string Data { get; } = data;
}