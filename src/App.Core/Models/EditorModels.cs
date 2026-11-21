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


/// <summary>Ce qui s'est passé quand un fichier ouvert a changé en dehors de l'éditeur.</summary>
public enum ExternalChangeKind
{
    /// <summary>Le document était propre : il a été rechargé depuis le disque (événement DocumentChanged également levé).</summary>
    Reloaded,

    /// <summary>Le document a des modifications non enregistrées et le disque a changé : rien n'a été écrasé.</summary>
    Conflict,

    /// <summary>Le fichier a été supprimé ou renommé. Le contenu reste en mémoire.</summary>
    Deleted
}

public sealed class ExternalChangeEventArgs(DocumentId id, string path, ExternalChangeKind kind) : EventArgs
{
    public DocumentId Id { get; } = id;
    public string Path { get; } = path;
    public ExternalChangeKind Kind { get; } = kind;
}
