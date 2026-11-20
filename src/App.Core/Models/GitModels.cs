namespace App.Core.Models;

public enum GitFileState { Untracked, Modified, Added, Deleted, Renamed, Conflicted }

public sealed record GitFileStatus(string Path, GitFileState State, bool IsStaged);

public sealed record GitStatus(string Branch, IReadOnlyList<GitFileStatus> Files);

public sealed record GitBranch(string Name, bool IsCurrent);

public sealed record GitCommitInfo(
    string Hash,
    string Author,
    DateTimeOffset Date,
    string Message);
