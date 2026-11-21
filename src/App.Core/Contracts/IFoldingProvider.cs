using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Pliage de code. Utilisé par le contrôle d'édition.</summary>
public interface IFoldingProvider
{
    /// <summary>Plages repliables du document, triées par ligne de début. À relire après chaque modification.</summary>
    IReadOnlyList<FoldingRange> GetFoldingRanges(DocumentId documentId);
}