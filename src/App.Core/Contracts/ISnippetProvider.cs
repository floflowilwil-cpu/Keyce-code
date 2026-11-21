using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Snippets de code, proposés comme des suggestions de complétion.</summary>
public interface ISnippetProvider
{
    /// <summary>
    /// Snippets du langage dont le mot-clé commence par le préfixe (sans tenir compte de la casse).
    /// InsertText utilise \n et une indentation relative de 4 espaces : l'éditeur la recale
    /// sur l'indentation de la ligne courante.
    /// </summary>
    IReadOnlyList<CompletionItem> GetSnippets(string languageId, string prefix);
}