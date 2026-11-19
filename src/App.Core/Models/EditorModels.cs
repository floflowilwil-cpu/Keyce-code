namespace App.Core.Models;

public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId New() => new(Guid.NewGuid());
}

public sealed record DocumentInfo(DocumentId Id, string? Path, string LanguageId, bool IsDirty);

public sealed class DocumentChangedEventArgs(DocumentId id, IReadOnlyList<TextEdit> edits) : EventArgs
{
    public DocumentId Id { get; } = id;
    public IReadOnlyList<TextEdit> Edits { get; } = edits;
}
