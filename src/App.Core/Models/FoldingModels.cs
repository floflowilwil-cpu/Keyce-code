namespace App.Core.Models;

/// <summary>Plage repliable : de StartLine à EndLine incluses (base 0). La ligne de début reste visible.</summary>
public readonly record struct FoldingRange(int StartLine, int EndLine);