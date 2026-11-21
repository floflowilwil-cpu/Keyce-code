using App.Core.Models;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace App.Languages;

/// <summary>Charge les grammaires TextMate, une seule fois par langage.</summary>
public sealed class GrammarProvider
{
    private readonly RegistryOptions _options = new(ThemeName.DarkPlus);
    private readonly Registry _registry;
    private readonly Dictionary<string, IGrammar?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GrammarProvider() => _registry = new Registry(_options);

    /// <summary>Grammaire du langage, ou null s'il n'est pas pris en charge.</summary>
    public IGrammar? Get(string languageId)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(languageId, out var grammar)) return grammar;

            var scope = _options.GetScopeByLanguageId(languageId);
            grammar = string.IsNullOrEmpty(scope) ? null : _registry.LoadGrammar(scope);
            _cache[languageId] = grammar;
            return grammar;
        }
    }
}

/// <summary>
/// Colore un document. Garde le texte et l'état de chaque ligne : la couleur d'une ligne
/// dépend des précédentes (chaîne ou commentaire sur plusieurs lignes).
/// Lignes en base 0, colonnes en UTF-16, texte en \n.
/// </summary>
public sealed class DocumentTokenizer
{
    private static readonly TimeSpan LineTimeLimit = TimeSpan.FromMilliseconds(500);

    private readonly IGrammar? _grammar;
    private readonly List<string> _lines = [""];
    private readonly List<IStateStack?> _endStates = [null];
    private readonly List<IReadOnlyList<SyntaxToken>> _tokens = [Array.Empty<SyntaxToken>()];
    private readonly object _gate = new();

    public DocumentTokenizer(IGrammar? grammar) => _grammar = grammar;

    public int LineCount { get { lock (_gate) return _lines.Count; } }

    /// <summary>Remplace tout le texte. Renvoie la plage de lignes à redessiner.</summary>
    public (int First, int Last) SetText(string text)
    {
        lock (_gate)
        {
            _lines.Clear();
            _lines.AddRange(text.Split('\n'));

            _endStates.Clear();
            _tokens.Clear();
            for (var i = 0; i < _lines.Count; i++)
            {
                _endStates.Add(null);
                _tokens.Add(Array.Empty<SyntaxToken>());
            }

            Retokenize(0, int.MaxValue);
            return (0, _lines.Count - 1);
        }
    }

    /// <summary>Applique des modifications, dans l'ordre. Renvoie la plage de lignes à redessiner.</summary>
    public (int First, int Last) ApplyEdits(IReadOnlyList<TextEdit> edits)
    {
        lock (_gate)
        {
            var first = int.MaxValue;
            var last = 0;
            var lineCountChanged = false;

            foreach (var edit in edits)
            {
                var startLine = Math.Clamp(edit.Range.Start.Line, 0, _lines.Count - 1);
                var endLine = Math.Clamp(edit.Range.End.Line, startLine, _lines.Count - 1);

                var startText = _lines[startLine];
                var endText = _lines[endLine];
                var head = startText[..Math.Min(edit.Range.Start.Column, startText.Length)];
                var tail = endText[Math.Min(edit.Range.End.Column, endText.Length)..];

                var newLines = (head + edit.NewText + tail).Split('\n');
                var removed = endLine - startLine + 1;

                _lines.RemoveRange(startLine, removed);
                _lines.InsertRange(startLine, newLines);
                _endStates.RemoveRange(startLine, removed);
                _endStates.InsertRange(startLine, new IStateStack?[newLines.Length]);
                _tokens.RemoveRange(startLine, removed);
                _tokens.InsertRange(startLine,
                    Enumerable.Repeat<IReadOnlyList<SyntaxToken>>(Array.Empty<SyntaxToken>(), newLines.Length));

                if (newLines.Length != removed) lineCountChanged = true;

                var lastEdited = startLine + newLines.Length - 1;
                var lastChanged = Retokenize(startLine, lastEdited);

                first = Math.Min(first, startLine);
                last = Math.Max(last, lastChanged);
            }

            // Si le nombre de lignes change, toutes les lignes suivantes se décalent.
            if (lineCountChanged) last = _lines.Count - 1;

            return edits.Count == 0 ? (0, -1) : (first, last);
        }
    }

    /// <summary>Jetons d'une ligne, triés par colonne. Vide si la ligne n'existe pas.</summary>
    public IReadOnlyList<SyntaxToken> GetTokens(int line)
    {
        lock (_gate)
            return line >= 0 && line < _tokens.Count ? _tokens[line] : Array.Empty<SyntaxToken>();
    }

    // Retraite à partir de 'from'. Après la dernière ligne modifiée, on s'arrête dès qu'une ligne
    // retrouve le même état de fin qu'avant : les suivantes n'ont pas changé.
    private int Retokenize(int from, int lastEdited)
    {
        var i = from;
        for (; i < _lines.Count; i++)
        {
            var oldEnd = _endStates[i];
            var previous = i == 0 ? null : _endStates[i - 1];

            var (tokens, end) = TokenizeLine(_lines[i], previous);
            _tokens[i] = tokens;
            _endStates[i] = end;

            if (i > lastEdited && Equals(oldEnd, end)) return i;
        }
        return _lines.Count - 1;
    }

    private (IReadOnlyList<SyntaxToken> Tokens, IStateStack? End) TokenizeLine(string line, IStateStack? previous)
    {
        if (_grammar is null) return (Array.Empty<SyntaxToken>(), null);

        var result = _grammar.TokenizeLine(line, previous, LineTimeLimit);
        var list = new List<SyntaxToken>();

        foreach (var token in result.Tokens)
        {
            var start = Math.Min(token.StartIndex, line.Length);
            var end = Math.Min(token.EndIndex, line.Length);   // TextMate ajoute parfois le \n final
            if (end <= start) continue;

            var kind = ScopeMapper.Map(token.Scopes);
            if (kind == TokenKind.Default) continue;           // rien à colorer

            list.Add(new SyntaxToken(start, end, kind));
        }
        return (list, result.RuleStack);
    }
}