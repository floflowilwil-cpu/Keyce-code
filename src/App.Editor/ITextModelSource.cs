using App.Core.Models;

namespace App.Editor;

/// <summary>
/// Donne un accès direct (sans copie) au <see cref="TextModel"/> d'un document.
/// Implémentée par <see cref="EditorService"/> ; les commandes d'édition l'utilisent pour éviter de recopier
/// tout le document à chaque frappe. Le modèle retourné est vivant : à lire sur le thread UI, et pas
/// après une modification si l'on a besoin de l'ancien état.
/// </summary>
public interface ITextModelSource
{
    TextModel GetTextModel(DocumentId id);
}
