using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>
/// Commandes d'édition et recherche sur un document ouvert. Utilisé par l'interface (clavier, menus,
/// palette de commandes). Toutes les commandes agissent sur le CaretSet courant du document
/// (<see cref="IEditorService.GetCaretSet"/>) et passent par l'historique : un Undo annule toute la commande.
/// </summary>
public interface IEditorCommands
{
    // ---- Saisie ----

    /// <summary>Insère le texte à chaque curseur, en remplaçant les sélections.</summary>
    void TypeText(DocumentId id, string text);

    /// <summary>Supprime la sélection ou le caractère précédent.</summary>
    void Backspace(DocumentId id);

    /// <summary>Supprime la sélection ou le caractère suivant.</summary>
    void DeleteForward(DocumentId id);

    /// <summary>Saut de ligne avec reprise de l'indentation.</summary>
    void InsertNewLine(DocumentId id);

    /// <summary>Tab : insère l'indentation, ou indente les lignes d'une sélection multi-lignes.</summary>
    void Indent(DocumentId id, string indentation = "\t");

    /// <summary>Maj+Tab : retire une tabulation ou jusqu'à 4 espaces au début des lignes concernées.</summary>
    void Outdent(DocumentId id);

    // ---- Lignes ----

    void CommentLines(DocumentId id, string prefix = "//");
    void UncommentLines(DocumentId id, string prefix = "//");
    void DuplicateLines(DocumentId id);
    void MoveLinesUp(DocumentId id);
    void MoveLinesDown(DocumentId id);

    // ---- Sélection et presse-papiers ----

    /// <summary>Sélectionne tout le document (un seul curseur).</summary>
    void SelectAll(DocumentId id);

    /// <summary>
    /// Texte à placer dans le presse-papiers : les sélections (séparées par \n), ou, si aucun curseur
    /// n'a de sélection, les lignes des curseurs terminées par \n. Ne modifie pas le document.
    /// </summary>
    string Copy(DocumentId id);

    /// <summary>Comme <see cref="Copy"/>, puis supprime les sélections (ou les lignes entières).</summary>
    string Cut(DocumentId id);

    /// <summary>
    /// Colle le texte. Avec plusieurs curseurs et autant de lignes que de curseurs, chaque curseur reçoit
    /// sa ligne ; sinon chaque curseur reçoit le texte entier.
    /// </summary>
    void Paste(DocumentId id, string text);

    // ---- Recherche dans le document ----

    /// <summary>Toutes les occurrences. Lève InvalidOperationException si une expression régulière dépasse son délai.</summary>
    IReadOnlyList<TextRange> FindAll(DocumentId id, string pattern, SearchOptions? options = null);

    /// <summary>Première occurrence à partir de la position donnée (retour au début si WrapAround).</summary>
    TextRange? FindNext(DocumentId id, string pattern, TextPosition from, SearchOptions? options = null);

    /// <summary>Dernière occurrence qui commence avant la position donnée (retour à la fin si WrapAround).</summary>
    TextRange? FindPrevious(DocumentId id, string pattern, TextPosition from, SearchOptions? options = null);

    /// <summary>
    /// Remplace l'occurrence à partir du début de la sélection principale, puis sélectionne la suivante.
    /// False s'il n'y a rien à remplacer.
    /// </summary>
    bool ReplaceNext(DocumentId id, string pattern, string replacement, SearchOptions? options = null);

    /// <summary>Remplace toutes les occurrences en une seule étape Undo. Retourne leur nombre.</summary>
    int ReplaceAll(DocumentId id, string pattern, string replacement, SearchOptions? options = null);
}
