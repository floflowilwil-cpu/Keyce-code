
namespace App.Core.Models;

public readonly record struct TerminalSessionId(Guid Value)
{
    public static TerminalSessionId New() => new(Guid.NewGuid());
}

/// <param name="Shell">Shell à lancer (powershell, cmd, bash...). Null = shell par défaut du système.</param>
public sealed record TerminalStartOptions(
    string? Shell = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed class TerminalOutputEventArgs(
    TerminalSessionId sessionId,
    string data) : EventArgs
{
    public TerminalSessionId SessionId { get; } = sessionId;
    public string Data { get; } = data;
}

public sealed class TerminalOutputBytesEventArgs(
    TerminalSessionId sessionId,
    byte[] data) : EventArgs
{
    public TerminalSessionId SessionId { get; } = sessionId;
    public byte[] Data { get; } = data;
}
