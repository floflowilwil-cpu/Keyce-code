namespace App.Core.Models;

public enum FileChangeKind { Created, Changed, Deleted, Renamed }

public sealed record FileChange(string Path, FileChangeKind Kind, string? OldPath = null);
