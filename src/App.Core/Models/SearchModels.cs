namespace App.Core.Models;

/// <summary>
/// Options de recherche dans un document.
/// </summary>
public sealed record SearchOptions(
    bool MatchCase = false,
    bool WholeWord = false,
    bool UseRegex = false,
    bool WrapAround = true,
    int RegexTimeoutMilliseconds = 1000
);
