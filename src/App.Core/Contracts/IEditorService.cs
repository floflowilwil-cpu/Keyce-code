using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Documents et édition. Utilisé par UI, langages, Git.</summary>
public interface IEditorService
{
    Task<DocumentInfo> OpenDocumentAsync(string path, CancellationToken ct = default);
    string GetText(DocumentId id);
    void ApplyEdit(DocumentId id, TextEdit edit);

    /// <summary>
    /// Applique un lot atomique de modifications exprimées dans le même état initial.
    /// Toutes les modifications appartiennent à une seule étape Undo.
    /// </summary>
    void ApplyEdits(DocumentId id, IReadOnlyList<TextEdit> edits);

    CaretSet GetCaretSet(DocumentId id);
    void SetCaretSet(DocumentId id, CaretSet carets);

    void Undo(DocumentId id);
    void Redo(DocumentId id);
    Task SaveAsync(DocumentId id, CancellationToken ct = default);

    /// <summary>Ferme le document (arrête aussi la surveillance du fichier). False si déjà fermé.</summary>
    bool CloseDocument(DocumentId id);

    DocumentInfo GetInfo(DocumentId id);

    /// <summary>Documents ouverts (pour les onglets).</summary>
    IReadOnlyList<DocumentInfo> GetOpenDocuments();

    /// <summary>
    /// Vérifie si le fichier a changé sur le disque et réagit (rechargement, conflit ou suppression).
    /// Le watcher le fait automatiquement ; l'interface peut l'appeler au retour de focus de la fenêtre.
    /// </summary>
    Task CheckExternalChangeAsync(DocumentId id, CancellationToken ct = default);

    /// <summary>Résout un conflit en abandonnant les modifications locales (recharge le disque).</summary>
    Task ReloadFromDiskAsync(DocumentId id, CancellationToken ct = default);

    /// <summary>Résout un conflit en gardant la version locale (la prochaine sauvegarde écrase le disque).</summary>
    Task KeepLocalVersionAsync(DocumentId id, CancellationToken ct = default);

    /// <summary>Le texte a changé (frappe, Undo/Redo, rechargement). Peut être levé depuis un autre thread.</summary>
    event EventHandler<DocumentChangedEventArgs> DocumentChanged;

    /// <summary>L'état du document a changé (modifié / enregistré).</summary>
    event EventHandler<DocumentInfo> DocumentStateChanged;

    /// <summary>Un fichier ouvert a changé sur le disque : rechargé, en conflit ou supprimé.</summary>
    event EventHandler<ExternalChangeEventArgs> ExternalChange;
}
