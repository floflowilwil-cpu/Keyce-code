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

    event EventHandler<DocumentChangedEventArgs> DocumentChanged;
}
