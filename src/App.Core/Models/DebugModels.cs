namespace App.Core.Models;

public readonly record struct DebugSessionId(Guid Value)
{
    public static DebugSessionId New() => new(Guid.NewGuid());
}

public sealed record DebugStartOptions(
    string Program,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record Breakpoint(string Path, int Line);

public enum StepKind { Over, Into, Out, Continue }
