using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Documents et édition. Utilisé par UI, langages, Git.</summary>
public interface IEditorService
{
    Task<DocumentInfo> OpenDocumentAsync(string path, CancellationToken ct = default);
    string GetText(DocumentId id);
    void ApplyEdit(DocumentId id, TextEdit edit);
    void Undo(DocumentId id);
    void Redo(DocumentId id);
    Task SaveAsync(DocumentId id, CancellationToken ct = default);

    event EventHandler<DocumentChangedEventArgs> DocumentChanged;
}
