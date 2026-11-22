using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Documents et édition. Utilisé par UI, langages, Git.</summary>
public interface IEditorService
{
    Task<DocumentInfo> OpenDocumentAsync(string path, CancellationToken ct = default);
    string GetText(DocumentId id);
    /// <summary>Nombre de lignes (au moins 1). Sert à l'affichage : ne copie pas le texte.</summary>
    int GetLineCount(DocumentId id);

    /// <summary>Texte d'une ligne (base 0), sans saut de ligne. Sert à l'affichage : ne copie pas tout le document.</summary>
    string GetLineText(DocumentId id, int line);

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

    /// <summary>
    /// Crée un document vide sans fichier (« Sans titre »). SaveAsync échoue tant qu'il n'a pas de chemin :
    /// utiliser <see cref="SaveAsAsync"/>.
    /// </summary>
    DocumentInfo CreateDocument();

    /// <summary>
    /// « Enregistrer sous » : écrit le contenu dans le fichier, associe le document à ce chemin
    /// (son langage est recalculé) et le marque enregistré. Lève Conflict si ce fichier est déjà ouvert
    /// dans un autre document. DocumentStateChanged est levé avec le nouveau chemin.
    /// </summary>
    Task SaveAsAsync(DocumentId id, string path, CancellationToken ct = default);

    /// <summary>Encodage utilisé pour enregistrer le document (UTF-8 si le service de fichiers ne le détecte pas).</summary>
    FileEncoding GetEncoding(DocumentId id);

    /// <summary>
    /// Change l'encodage d'enregistrement (« Enregistrer avec l'encodage… »). Ne modifie pas le texte et ne marque pas
    /// le document modifié : l'interface appelle ensuite SaveAsync. Lève InvalidArgument pour un encodage inconnu.
    /// DocumentStateChanged est levé.
    /// </summary>
    void SetEncoding(DocumentId id, FileEncoding encoding);

    /// <summary>
    /// Renomme ou déplace un fichier ou un dossier sur le disque ET met à jour les documents ouverts qui s'y trouvent
    /// (ils restent ouverts, avec leur historique et leurs modifications non enregistrées, sous le nouveau chemin).
    /// Retourne l'état des documents déplacés. DocumentStateChanged est levé pour chacun.
    /// Lève NotFound, AlreadyExists ou InvalidArgument comme <see cref="IFileService.MoveAsync"/>.
    /// </summary>
    Task<IReadOnlyList<DocumentInfo>> MovePathAsync(string oldPath, string newPath, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>
    /// Relit le fichier du disque en forçant l'encodage (« Rouvrir avec l'encodage… ») : à utiliser quand la détection
    /// automatique s'est trompée. Remplace le texte, vide l'historique Undo et marque le document propre.
    /// Lève InvalidArgument (document sans fichier, encodage inconnu, service de fichiers sans encodage) ou Conflict
    /// si le document a des modifications non enregistrées. DocumentChanged et DocumentStateChanged sont levés.
    /// </summary>
    Task ReopenWithEncodingAsync(DocumentId id, FileEncoding encoding, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>Fin de ligne utilisée à l'enregistrement : celle du fichier ouvert (détectée), ou celle de la plateforme.</summary>
    LineEndingStyle GetLineEnding(DocumentId id) => throw new NotSupportedException();

    /// <summary>
    /// Change la fin de ligne d'enregistrement (CRLF ⇄ LF). Le texte en mémoire ne change pas, mais le document
    /// est « modifié » tant que la fin de ligne diffère de celle du fichier enregistré. DocumentStateChanged est levé.
    /// </summary>
    void SetLineEnding(DocumentId id, LineEndingStyle style) => throw new NotSupportedException();

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

    /// <summary>
    /// Un document vient d'être ouvert ou créé (OpenDocumentAsync sur un fichier pas encore ouvert, CreateDocument).
    /// Un nouvel abonné lit GetOpenDocuments() pour les documents déjà ouverts.
    /// </summary>
    event EventHandler<DocumentInfo> DocumentOpened;

    /// <summary>Un document vient d'être fermé (CloseDocument). L'info est celle du document juste avant sa fermeture.</summary>
    event EventHandler<DocumentInfo> DocumentClosed;

    /// <summary>L'état du document a changé (modifié / enregistré).</summary>
    event EventHandler<DocumentInfo> DocumentStateChanged;

    /// <summary>Un fichier ouvert a changé sur le disque : rechargé, en conflit ou supprimé.</summary>
    event EventHandler<ExternalChangeEventArgs> ExternalChange;
}
