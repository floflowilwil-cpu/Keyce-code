using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using App.Core.Contracts;
using App.Core.Models;

namespace App.Desktop.Controls;

/// <summary>
/// Auto-complétion : liste de suggestions dessinée sous le curseur.
/// Sources, dans cet ordre : serveur de langage (LSP), snippets, puis mots déjà présents dans le document
/// (la liste reste donc utile même si aucun serveur de langage n'est installé).
/// Ouverture automatique en tapant, ou avec Ctrl+Espace. Navigation : ↑ ↓, Entrée ou Tab pour valider, Échap pour fermer.
/// </summary>
public sealed partial class CodeEditorControl
{
    private const int MaxVisibleCompletions = 10;
    private const int MaxCompletionItems = 200;
    private const int MaxScannedChars = 300_000;
    private const string SnippetKind = "Snippet";
    private const string WordKind = "Mot";

    private static readonly Regex WordRegex = new(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);
    private static readonly Regex SnippetPlaceholder = new(@"\$\{\d+(?::([^}]*))?\}|\$\d+", RegexOptions.Compiled);

    private ILanguageClient? _languages;
    private ISnippetProvider? _snippets;
    private bool _completionEnabled;

    private List<CompletionItem> _completionSource = new();
    private List<CompletionItem> _completionItems = new();
    private int _completionSelected;
    private int _completionTop;
    private TextPosition _completionAnchor;
    private bool _completionOpen;
    private Rect _completionRect;
    private double _completionRowHeight;
    private CancellationTokenSource? _completionCts;

    private readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private static readonly IBrush DarkPopupBackground = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush DarkPopupSelection = new SolidColorBrush(Color.Parse("#094771"));
    private static readonly IBrush DarkPopupBorder = new SolidColorBrush(Color.Parse("#454545"));
    private static readonly IBrush DarkPopupText = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush DarkPopupMuted = new SolidColorBrush(Color.Parse("#9D9D9D"));
    private static readonly IBrush LightPopupBackground = new SolidColorBrush(Color.Parse("#F3F3F3"));
    private static readonly IBrush LightPopupSelection = new SolidColorBrush(Color.Parse("#CCE4F7"));
    private static readonly IBrush LightPopupBorder = new SolidColorBrush(Color.Parse("#C8C8C8"));
    private static readonly IBrush LightPopupText = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush LightPopupMuted = new SolidColorBrush(Color.Parse("#6E6E6E"));

    // ------------------------------------------------------------------ API pour l'interface

    /// <summary>
    /// Active l'auto-complétion. Les deux sources sont facultatives : sans elles, la liste propose
    /// les mots du document.
    /// </summary>
    public void AttachCompletion(ILanguageClient? languages, ISnippetProvider? snippets)
    {
        _languages = languages;
        _snippets = snippets;

        if (!_completionEnabled)
        {
            _completionEnabled = true;
            _completionTimer.Tick += (_, _) =>
            {
                _completionTimer.Stop();
                _ = RequestCompletionAsync(manual: false);
            };
            LostFocus += (_, _) => HideCompletion();
        }
    }

    // ------------------------------------------------------------------ déclenchement

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Appelé après chaque caractère saisi.</summary>
    private void OnTextTypedForCompletion(string text)
    {
        if (!_completionEnabled)
            return;

        if (text.Length == 1 && (IsWordChar(text[0]) || text[0] == '.'))
        {
            _completionTimer.Stop();
            _completionTimer.Start();
        }
        else
        {
            HideCompletion();
        }
    }

    private async Task RequestCompletionAsync(bool manual)
    {
        if (!_completionEnabled || _editor is null || _documentId is not { } id)
            return;

        _completionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _completionCts = cts;
        CancellationToken ct = cts.Token;

        Caret caret = _editor.GetCaretSet(id).Primary;
        if (caret.HasSelection)
        {
            HideCompletion();
            return;
        }

        TextPosition position = caret.Position;
        string line = _editor.GetLineText(id, position.Line);
        int column = Math.Clamp(position.Column, 0, line.Length);

        int start = column;
        while (start > 0 && IsWordChar(line[start - 1]))
            start--;

        string prefix = line.Substring(start, column - start);
        bool afterDot = start > 0 && line[start - 1] == '.';

        // Sans début de mot, on n'ouvre la liste qu'après un « . » ou sur demande (Ctrl+Espace).
        if (prefix.Length == 0 && !manual && !afterDot)
        {
            HideCompletion();
            return;
        }

        DocumentInfo info = _editor.GetInfo(id);
        var items = new List<CompletionItem>();

        if (_languages is not null && info.Path is not null)
        {
            try
            {
                IReadOnlyList<CompletionItem> fromServer =
                    await _languages.CompletionAsync(info.Path, position, ct);

                foreach (CompletionItem item in fromServer)
                {
                    string kind = item.Kind ?? "";
                    items.Add(kind == SnippetKind
                        ? item
                        : item with { InsertText = StripSnippetSyntax(item.InsertText) });
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Serveur absent ou en erreur : on continue avec les snippets et les mots du document.
            }
        }

        if (ct.IsCancellationRequested || _documentId != id)
            return;

        if (_snippets is not null && !afterDot)
            items.AddRange(_snippets.GetSnippets(info.LanguageId, prefix));

        items.AddRange(DocumentWords(id, prefix));

        // L'utilisateur a pu continuer à taper pendant l'attente.
        Caret now = _editor.GetCaretSet(id).Primary;
        if (now.HasSelection || now.Position != position)
            return;

        _completionSource = Deduplicate(items);
        _completionAnchor = new TextPosition(position.Line, start);
        ApplyCompletionFilter(prefix);

        if (_completionItems.Count == 0 || IsOnlyExactMatch(prefix))
        {
            HideCompletion();
            return;
        }

        _completionOpen = true;
        InvalidateVisual();
    }

    private IEnumerable<CompletionItem> DocumentWords(DocumentId id, string prefix)
    {
        string text = _editor!.GetText(id);
        if (text.Length > MaxScannedChars)
            return Enumerable.Empty<CompletionItem>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<CompletionItem>();

        foreach (Match match in WordRegex.Matches(text))
        {
            string word = match.Value;

            // Le mot en cours de saisie ne doit pas se proposer lui-même.
            if (word == prefix)
                continue;

            if (seen.Add(word))
                words.Add(new CompletionItem(word, word, WordKind));
        }

        return words;
    }

    private static List<CompletionItem> Deduplicate(IEnumerable<CompletionItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CompletionItem>();

        foreach (CompletionItem item in items)
        {
            string key = item.Label + (item.Kind == SnippetKind ? "#snippet" : "");

            if (seen.Add(key))
                result.Add(item);
        }

        return result;
    }

    private static string StripSnippetSyntax(string text) =>
        SnippetPlaceholder.Replace(text, match => match.Groups[1].Success ? match.Groups[1].Value : "");

    // ------------------------------------------------------------------ filtrage

    private void ApplyCompletionFilter(string prefix)
    {
        _completionItems = _completionSource
            .Select(item => (Item: item, Rank: Rank(item.Label, prefix)))
            .Where(entry => entry.Rank >= 0)
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => KindOrder(entry.Item))
            .ThenBy(entry => entry.Item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompletionItems)
            .Select(entry => entry.Item)
            .ToList();

        _completionSelected = 0;
        _completionTop = 0;
    }

    private static int Rank(string label, string prefix)
    {
        if (prefix.Length == 0) return 0;
        if (label.StartsWith(prefix, StringComparison.Ordinal)) return 0;
        if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 1;
        if (prefix.Length >= 2 && label.Contains(prefix, StringComparison.OrdinalIgnoreCase)) return 2;
        return -1;
    }

    private static int KindOrder(CompletionItem item) =>
        item.Kind == WordKind ? 2 : item.Kind == SnippetKind ? 1 : 0;

    private bool IsOnlyExactMatch(string prefix) =>
        _completionItems.Count == 1
        && _completionItems[0].Label == prefix
        && _completionItems[0].Kind != SnippetKind;

    /// <summary>Appelé après chaque commande : referme la liste si le curseur a quitté le mot, sinon la filtre.</summary>
    private void ValidateCompletion()
    {
        if (!_completionOpen)
            return;

        if (_editor is null || _documentId is not { } id)
        {
            HideCompletion();
            return;
        }

        Caret caret = _editor.GetCaretSet(id).Primary;

        if (caret.HasSelection
            || caret.Position.Line != _completionAnchor.Line
            || caret.Position.Column < _completionAnchor.Column)
        {
            HideCompletion();
            return;
        }

        string line = _editor.GetLineText(id, caret.Position.Line);

        if (_completionAnchor.Column > line.Length)
        {
            HideCompletion();
            return;
        }

        int column = Math.Min(caret.Position.Column, line.Length);
        string prefix = line.Substring(_completionAnchor.Column, column - _completionAnchor.Column);

        if (!prefix.All(IsWordChar))
        {
            HideCompletion();
            return;
        }

        ApplyCompletionFilter(prefix);

        if (_completionItems.Count == 0 || IsOnlyExactMatch(prefix))
            HideCompletion();
    }

    private void HideCompletion()
    {
        _completionTimer.Stop();
        _completionCts?.Cancel();

        if (!_completionOpen)
            return;

        _completionOpen = false;
        _completionRect = default;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ clavier et souris

    /// <summary>Touches de navigation quand la liste est ouverte. Retourne true si la touche est consommée.</summary>
    private bool HandleCompletionKey(KeyEventArgs e)
    {
        if (!_completionOpen)
            return false;

        const KeyModifiers blocking = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta | KeyModifiers.Shift;
        if ((e.KeyModifiers & blocking) != 0)
            return false;

        switch (e.Key)
        {
            case Key.Down:
                MoveCompletion(1);
                return true;
            case Key.Up:
                MoveCompletion(-1);
                return true;
            case Key.PageDown:
                MoveCompletion(MaxVisibleCompletions);
                return true;
            case Key.PageUp:
                MoveCompletion(-MaxVisibleCompletions);
                return true;
            case Key.Enter:
            case Key.Tab:
                AcceptCompletion();
                return true;
            case Key.Escape:
                HideCompletion();
                return true;
            default:
                return false;
        }
    }

    private void MoveCompletion(int delta)
    {
        int count = _completionItems.Count;
        if (count == 0)
            return;

        _completionSelected = Math.Abs(delta) == 1
            ? (_completionSelected + delta + count) % count
            : Math.Clamp(_completionSelected + delta, 0, count - 1);

        if (_completionSelected < _completionTop)
            _completionTop = _completionSelected;
        else if (_completionSelected >= _completionTop + MaxVisibleCompletions)
            _completionTop = _completionSelected - MaxVisibleCompletions + 1;

        InvalidateVisual();
    }

    /// <summary>Clic dans la liste : valide la suggestion cliquée.</summary>
    private bool HandleCompletionClick(Point point)
    {
        if (!_completionOpen || !_completionRect.Contains(point) || _completionRowHeight <= 0)
            return false;

        int row = (int)((point.Y - _completionRect.Y - 2) / _completionRowHeight);
        int index = _completionTop + row;

        if (row < 0 || index >= _completionItems.Count)
            return true;

        _completionSelected = index;
        AcceptCompletion();
        return true;
    }

    // ------------------------------------------------------------------ validation

    private void AcceptCompletion()
    {
        if (!_completionOpen
            || _editor is null
            || _documentId is not { } id
            || _completionSelected < 0
            || _completionSelected >= _completionItems.Count)
        {
            HideCompletion();
            return;
        }

        CompletionItem item = _completionItems[_completionSelected];
        TextPosition caret = _editor.GetCaretSet(id).Primary.Position;
        TextPosition anchor = _completionAnchor;

        HideCompletion();

        string line = _editor.GetLineText(id, caret.Line);
        string indent = new(line.TakeWhile(c => c == ' ' || c == '\t').ToArray());

        // Le moteur de texte travaille en \n ; on recale les lignes suivantes sur l'indentation courante.
        string text = item.InsertText.Replace("\r\n", "\n").Replace("\n", "\n" + indent);

        int caretOffset = text.Length;
        if (item.Kind == SnippetKind)
        {
            int blank = FirstBlankLineEnd(text);
            if (blank >= 0)
                caretOffset = blank;
        }

        TextPosition finalPosition = PositionAfter(anchor, text, caretOffset);

        Run(() =>
        {
            _editor.ApplyEdit(id, new TextEdit(new TextRange(anchor, caret), text));
            _editor.SetCaretSet(id, CaretSet.Single(finalPosition));
        });
    }

    /// <summary>Fin de la première ligne vide (indentation seule) d'un snippet : endroit naturel pour continuer à écrire.</summary>
    private static int FirstBlankLineEnd(string text)
    {
        string[] lines = text.Split('\n');
        int offset = 0;

        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (i > 0 && lines[i].Trim().Length == 0)
                return offset + lines[i].Length;

            offset += lines[i].Length + 1;
        }

        return -1;
    }

    private static TextPosition PositionAfter(TextPosition start, string text, int offset)
    {
        string before = text.Substring(0, offset);
        int lineBreaks = before.Count(c => c == '\n');

        if (lineBreaks == 0)
            return new TextPosition(start.Line, start.Column + before.Length);

        int lastBreak = before.LastIndexOf('\n');
        return new TextPosition(start.Line + lineBreaks, before.Length - lastBreak - 1);
    }

    // ------------------------------------------------------------------ dessin

    private void DrawCompletion(DrawingContext context, double textLeft)
    {
        _completionRect = default;

        if (!_completionOpen || _completionItems.Count == 0 || _editor is null)
            return;

        bool dark = _background is ISolidColorBrush solid
            && (0.299 * solid.Color.R + 0.587 * solid.Color.G + 0.114 * solid.Color.B) < 128;

        IBrush backgroundBrush = dark ? DarkPopupBackground : LightPopupBackground;
        IBrush selectionBrush = dark ? DarkPopupSelection : LightPopupSelection;
        IBrush borderBrush = dark ? DarkPopupBorder : LightPopupBorder;
        IBrush textBrush = dark ? DarkPopupText : LightPopupText;
        IBrush mutedBrush = dark ? DarkPopupMuted : LightPopupMuted;

        int visible = Math.Min(MaxVisibleCompletions, _completionItems.Count);
        double rowHeight = _lineHeight + 2;
        _completionRowHeight = rowHeight;

        int labelChars = Math.Min(48, _completionItems.Max(item => item.Label.Length));
        double width = Math.Min((labelChars + 12) * _charWidth + 24, Math.Max(120, Bounds.Width - 20));
        double height = visible * rowHeight + 4;

        double x = textLeft - _scrollX
            + VisualColumn(_completionAnchor.Line, _completionAnchor.Column) * _charWidth;
        double y = (_completionAnchor.Line + 1) * _lineHeight - _scrollY;

        if (y + height > Bounds.Height)
            y = _completionAnchor.Line * _lineHeight - _scrollY - height;
        if (x + width > Bounds.Width - ScrollbarWidth)
            x = Math.Max(0, Bounds.Width - ScrollbarWidth - width);

        var rect = new Rect(x, Math.Max(0, y), width, height);
        _completionRect = rect;

        context.DrawRectangle(backgroundBrush, new Pen(borderBrush, 1), rect, 3, 3);

        for (int row = 0; row < visible; row++)
        {
            int index = _completionTop + row;
            CompletionItem item = _completionItems[index];
            double rowY = rect.Y + 2 + row * rowHeight;

            if (index == _completionSelected)
                context.DrawRectangle(selectionBrush, null, new Rect(rect.X + 1, rowY, rect.Width - 2, rowHeight));

            string label = item.Label.Length > 48 ? item.Label[..47] + "…" : item.Label;
            context.DrawText(Format(label, textBrush), new Point(rect.X + 8, rowY + 1));

            if (!string.IsNullOrEmpty(item.Kind))
            {
                var kind = Format(item.Kind, mutedBrush);
                context.DrawText(kind, new Point(rect.Right - 8 - kind.Width, rowY + 1));
            }
        }
    }
}
