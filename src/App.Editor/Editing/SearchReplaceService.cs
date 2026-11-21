using App.Editor;
using System.Text.RegularExpressions;
using App.Core.Models;

namespace App.Editor.Editing;

/// <summary>
/// Résultat d'une recherche dans le document.
/// Les offsets sont exprimés en unités UTF-16.
/// </summary>
public sealed class SearchMatch
{
    internal Match RegexMatch { get; }

    public int Offset { get; }
    public int Length { get; }
    public string Value { get; }

    internal SearchMatch(Match match)
    {
        RegexMatch = match;
        Offset = match.Index;
        Length = match.Length;
        Value = match.Value;
    }
}

/// <summary>
/// Moteur de recherche et de préparation des remplacements.
/// Ne modifie pas directement le document.
/// </summary>
public sealed class SearchReplaceService
{
    private static readonly TimeSpan DefaultTimeout =
        TimeSpan.FromSeconds(1);

    /// <summary>
    /// Recherche toutes les occurrences d'un texte.
    /// </summary>
    public IReadOnlyList<SearchMatch> FindAll(
        TextModel document,
        string pattern,
        SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pattern);

        options ??= new SearchOptions();

        if (pattern.Length == 0)
            return Array.Empty<SearchMatch>();

        string text = document.GetText();
        Regex regex = BuildRegex(pattern, options);

        try
        {
            return regex.Matches(text)
                .Cast<Match>()
                .Where(m => m.Success)
                .Select(m => new SearchMatch(m))
                .ToArray();
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InvalidOperationException(
                "La recherche a dépassé le délai autorisé.");
        }
    }

    /// <summary>
    /// Recherche la prochaine occurrence à partir d'un offset.
    /// </summary>
    public SearchMatch? FindNext(
        TextModel document,
        string pattern,
        int startOffset,
        SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pattern);

        if (startOffset < 0 || startOffset > document.Length)
            throw new ArgumentOutOfRangeException(nameof(startOffset));

        options ??= new SearchOptions();
        if (pattern.Length == 0) return null;

        // On s'arrête à la première occurrence au lieu de chercher (et d'allouer) toutes les occurrences.
        string text = document.GetText();
        Regex regex = BuildRegex(pattern, options);

        try
        {
            Match match = regex.Match(text, startOffset);
            if (match.Success) return new SearchMatch(match);
            if (!options.WrapAround) return null;

            match = regex.Match(text, 0);
            return match.Success ? new SearchMatch(match) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InvalidOperationException(
                "La recherche a dépassé le délai autorisé.");
        }
    }

    /// <summary>
    /// Recherche l'occurrence précédente.
    /// </summary>
    public SearchMatch? FindPrevious(
        TextModel document,
        string pattern,
        int startOffset,
        SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (startOffset < 0 || startOffset > document.Length)
            throw new ArgumentOutOfRangeException(nameof(startOffset));

        options ??= new SearchOptions();

        var matches = FindAll(document, pattern, options);

        var previous = matches.LastOrDefault(
            m => m.Offset < startOffset);

        if (previous != null)
            return previous;

        return options.WrapAround
            ? matches.LastOrDefault()
            : null;
    }

    /// <summary>
    /// Prépare le remplacement d'une occurrence.
    /// </summary>
    public TextEdit? CreateReplaceEdit(
        TextModel document,
        SearchMatch match,
        string replacement,
        SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(replacement);

        options ??= new SearchOptions();

        // Vérifie que le résultat n'est pas devenu obsolète.
        if (match.Offset + match.Length > document.Length)
            return null;

        if (document.GetText(match.Offset, match.Length) != match.Value)
            return null;

        string newText = options.UseRegex
            ? match.RegexMatch.Result(replacement)
            : replacement;

        var start = document.GetPosition(match.Offset);
        var end = document.GetPosition(
            match.Offset + match.Length);

        return new TextEdit(
            new TextRange(start, end),
            newText);
    }

    /// <summary>
    /// Prépare le remplacement de toutes les occurrences.
    /// Les éditions sont retournées dans l'ordre décroissant
    /// des offsets afin de préserver les positions originales.
    /// </summary>
    public IReadOnlyList<TextEdit> CreateReplaceAllEdits(
        TextModel document,
        string pattern,
        string replacement,
        SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(replacement);

        options ??= new SearchOptions();

        var matches = FindAll(document, pattern, options);

        var edits = new List<TextEdit>(matches.Count);

        foreach (var match in matches.OrderByDescending(m => m.Offset))
        {
            var edit = CreateReplaceEdit(
                document,
                match,
                replacement,
                options);

            if (edit != null)
                edits.Add(edit);
        }

        return edits;
    }

    private static Regex BuildRegex(
        string pattern,
        SearchOptions options)
    {
        string expression = options.UseRegex
            ? pattern
            : Regex.Escape(pattern);

        if (options.WholeWord)
        {
            expression =
                $@"(?<![\p{{L}}\p{{N}}_])(?:{expression})(?![\p{{L}}\p{{N}}_])";
        }

        // Multiline : ^ et $ désignent le début et la fin de chaque ligne, comme dans un éditeur.
        RegexOptions regexOptions =
            RegexOptions.CultureInvariant | RegexOptions.Multiline;

        if (!options.MatchCase)
            regexOptions |= RegexOptions.IgnoreCase;

        TimeSpan timeout =
            options.RegexTimeoutMilliseconds > 0
                ? TimeSpan.FromMilliseconds(
                    options.RegexTimeoutMilliseconds)
                : DefaultTimeout;

        return new Regex(
            expression,
            regexOptions,
            timeout);
    }
}