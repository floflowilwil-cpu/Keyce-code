namespace App.Core.Models;

public sealed record WorkspaceInfo(string RootPath, string Name);

public sealed class WorkspaceChangedEventArgs(WorkspaceInfo? previous, WorkspaceInfo? current) : EventArgs
{
    public WorkspaceInfo? Previous { get; } = previous;
    public WorkspaceInfo? Current { get; } = current;
}
