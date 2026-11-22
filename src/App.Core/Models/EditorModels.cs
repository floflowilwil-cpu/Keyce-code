namespace App.Core.Models;

public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId New() => new(Guid.NewGuid());
}

/// <summary>
/// État d'un document. <see cref="Version"/> est un entier qui augmente à chaque modification du texte
/// (jamais de retour en arrière, même après Annuler) : il permet d'ignorer un événement plus ancien que
/// l'état déjà connu (par exemple un <see cref="DocumentChangedEventArgs"/> reçu pendant l'ouverture).
/// </summary>
public sealed record DocumentInfo(DocumentId Id, string? Path, string LanguageId, bool IsDirty, int Version = 0);

public sealed class DocumentChangedEventArgs(DocumentId id, IReadOnlyList<TextEdit> edits, int version = 0) : EventArgs
{
    public DocumentId Id { get; } = id;
    public IReadOnlyList<TextEdit> Edits { get; } = edits;

    /// <summary>
    /// Version du document APRÈS ces modifications (même échelle que <see cref="DocumentInfo.Version"/>).
    /// Un événement dont la version est inférieure ou égale à celle déjà connue est à ignorer.
    /// </summary>
    public int Version { get; } = version;
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
