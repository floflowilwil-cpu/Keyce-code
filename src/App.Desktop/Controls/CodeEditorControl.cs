using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Desktop.Controls;

/// <summary>
/// Contrôle d'édition de code : dessine le document d'un <see cref="IEditorService"/> (lignes visibles seulement),
/// transmet clavier et souris à <see cref="IEditorCommands"/> et colore avec un <see cref="ISyntaxHighlighter"/> (optionnel).
/// Le contrôle ne garde aucune copie du texte : tout passe par le service d'édition (multi-curseurs compris).
///
/// Utilisation :
///   editor.Attach(editorService, editorCommands, highlighter);
///   editor.DocumentId = (await editorService.OpenDocumentAsync(path)).Id;
///
/// Police à chasse fixe supposée (colonne × largeur d'un caractère). Les tabulations sont affichées en
/// <see cref="TabSize"/> colonnes. Pas encore géré : retour à la ligne automatique, IME, caractères larges.
/// </summary>
public sealed class CodeEditorControl : Control
{
    private const double GutterPadding = 12;
    private const double ScrollbarWidth = 10;

    private IEditorService? _editor;
    private IEditorCommands? _commands;
    private ISyntaxHighlighter? _highlighter;
    private DocumentId? _documentId;

    private FontFamily _fontFamily = new("Cascadia Mono, Consolas, monospace");
    private double _fontSize = 14;
    private int _tabSize = 4;
    private Typeface _typeface;
    private double _charWidth = 8;
    private double _lineHeight = 18;

    private IBrush _background = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private IBrush _foreground = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private IBrush _gutterForeground = new SolidColorBrush(Color.Parse("#858585"));
    private IBrush _selectionBrush = new SolidColorBrush(Color.Parse("#264F78"));
    private IBrush _currentLineBrush = new SolidColorBrush(Color.Parse("#2A2D2E"));

    private double _scrollX;
    private double _scrollY;
    private double _contentWidth;       // plus large ligne affichée jusqu'ici (pixels)
    private Rect _scrollThumb;
    private bool _draggingText;
    private bool _draggingThumb;
    private double _thumbGrabOffset;

    private readonly DispatcherTimer _blinkTimer = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool _caretOn = true;
    private bool _subscribed;

    public CodeEditorControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        _typeface = new Typeface(_fontFamily);
        RecalculateMetrics();

        _blinkTimer.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            InvalidateVisual();
        };
        GotFocus += (_, _) => { ResetBlink(); _blinkTimer.Start(); InvalidateVisual(); };
        LostFocus += (_, _) => { _blinkTimer.Stop(); _caretOn = false; InvalidateVisual(); };
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    // ------------------------------------------------------------------ API pour l'interface

    /// <summary>Branche le contrôle aux services. Le colorateur est facultatif.</summary>
    public void Attach(IEditorService editor, IEditorCommands commands, ISyntaxHighlighter? highlighter = null)
    {
        Unsubscribe();
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _highlighter = highlighter;
        if (this.IsAttachedToVisualTree()) Subscribe();
        InvalidateVisual();
    }

    /// <summary>Document affiché (null : rien). Remet le défilement en haut.</summary>
    public DocumentId? DocumentId
    {
        get => _documentId;
        set
        {
            _documentId = value;
            _scrollX = _scrollY = 0;
            _contentWidth = 0;
            InvalidateVisual();
            CaretChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Position du curseur principal (pour la barre d'état : Ln = Line + 1, Col = Column + 1).</summary>
    public TextPosition CaretPosition =>
        _editor is not null && _documentId is { } id ? _editor.GetCaretSet(id).Primary.Position : new TextPosition(0, 0);

    /// <summary>Le curseur ou la sélection a changé.</summary>
    public event EventHandler? CaretChanged;

    /// <summary>Ctrl+S : à l'interface d'enregistrer (ou de demander « Enregistrer sous » si le document n'a pas de nom).</summary>
    public event EventHandler? SaveRequested;

    /// <summary>Une commande a été refusée par le moteur (message à afficher dans la barre d'état).</summary>
    public event EventHandler<string>? EditRejected;

    public FontFamily EditorFontFamily
    {
        get => _fontFamily;
        set { _fontFamily = value; _typeface = new Typeface(value); RecalculateMetrics(); InvalidateVisual(); }
    }

    public double EditorFontSize
    {
        get => _fontSize;
        set { _fontSize = Math.Max(6, value); RecalculateMetrics(); InvalidateVisual(); }
    }

    public int TabSize
    {
        get => _tabSize;
        set { _tabSize = Math.Clamp(value, 1, 16); InvalidateVisual(); }
    }

    public IBrush EditorBackground { get => _background; set { _background = value; InvalidateVisual(); } }
    public IBrush EditorForeground { get => _foreground; set { _foreground = value; InvalidateVisual(); } }
    public IBrush GutterForeground { get => _gutterForeground; set { _gutterForeground = value; InvalidateVisual(); } }
    public IBrush SelectionBrush { get => _selectionBrush; set { _selectionBrush = value; InvalidateVisual(); } }
    public IBrush CurrentLineBrush { get => _currentLineBrush; set { _currentLineBrush = value; InvalidateVisual(); } }

    /// <summary>Couleur de chaque catégorie de jeton (thème). Modifiable par l'interface.</summary>
    public Dictionary<TokenKind, IBrush> TokenBrushes { get; } = DefaultTokenBrushes();

    private static Dictionary<TokenKind, IBrush> DefaultTokenBrushes()
    {
        static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
        return new Dictionary<TokenKind, IBrush>
        {
            [TokenKind.Keyword] = B("#569CD6"),
            [TokenKind.Type] = B("#4EC9B0"),
            [TokenKind.Function] = B("#DCDCAA"),
            [TokenKind.Variable] = B("#9CDCFE"),
            [TokenKind.Property] = B("#9CDCFE"),
            [TokenKind.Parameter] = B("#9CDCFE"),
            [TokenKind.Constant] = B("#4FC1FF"),
            [TokenKind.Namespace] = B("#4EC9B0"),
            [TokenKind.Preprocessor] = B("#C586C0"),
            [TokenKind.Operator] = B("#D4D4D4"),
            [TokenKind.Punctuation] = B("#D4D4D4"),
            [TokenKind.String] = B("#CE9178"),
            [TokenKind.StringEscape] = B("#D7BA7D"),
            [TokenKind.Number] = B("#B5CEA8"),
            [TokenKind.Comment] = B("#6A9955"),
            [TokenKind.Tag] = B("#569CD6"),
            [TokenKind.Attribute] = B("#9CDCFE"),
            [TokenKind.Heading] = B("#569CD6"),
            [TokenKind.Bold] = B("#569CD6"),
            [TokenKind.Italic] = B("#C586C0"),
            [TokenKind.Link] = B("#3794FF"),
            [TokenKind.Code] = B("#CE9178"),
            [TokenKind.Error] = B("#F44747"),
        };
    }

    // ------------------------------------------------------------------ abonnements aux services

    private void Subscribe()
    {
        if (_subscribed || _editor is null) return;
        _editor.DocumentChanged += OnDocumentChanged;
        if (_highlighter is not null) _highlighter.TokensInvalidated += OnTokensInvalidated;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        if (_editor is not null) _editor.DocumentChanged -= OnDocumentChanged;
        if (_highlighter is not null) _highlighter.TokensInvalidated -= OnTokensInvalidated;
        _subscribed = false;
    }

    // Ces événements peuvent arriver d'un autre thread (rechargement externe, colorateur).
    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_documentId is not { } id || e.Id != id) return;
        Dispatcher.UIThread.Post(() =>
        {
            ClampScroll();
            InvalidateVisual();
            CaretChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTokensInvalidated(object? sender, TokensInvalidatedEventArgs e)
    {
        if (_documentId is not { } id || e.DocumentId != id) return;
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    // ------------------------------------------------------------------ mesures

    private FormattedText Format(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, brush);

    private void RecalculateMetrics()
    {
        var sample = Format(new string('M', 20), _foreground);
        _charWidth = Math.Max(1, sample.Width / 20);
        _lineHeight = Math.Max(1, Math.Ceiling(sample.Height));
    }

    private int LineCount() => _editor is not null && _documentId is { } id ? _editor.GetLineCount(id) : 0;

    private double GutterWidth(int lineCount) =>
        (Math.Max(2, lineCount.ToString(CultureInfo.InvariantCulture).Length) * _charWidth) + GutterPadding * 2;

    private double TextLeft(int lineCount) => GutterWidth(lineCount) + 4;

    /// <summary>Texte affiché d'une ligne (tabulations développées) et colonne affichée de chaque colonne logique.</summary>
    private (string Display, int[] Map) Expand(string raw)
    {
        var map = new int[raw.Length + 1];
        var sb = new StringBuilder(raw.Length + 8);
        for (int i = 0; i < raw.Length; i++)
        {
            map[i] = sb.Length;
            if (raw[i] == '\t') sb.Append(' ', _tabSize - (sb.Length % _tabSize));
            else sb.Append(raw[i]);
        }
        map[raw.Length] = sb.Length;
        return (sb.ToString(), map);
    }

    private int VisualColumn(int line, int column)
    {
        if (_editor is null || _documentId is not { } id) return column;
        var raw = _editor.GetLineText(id, line);
        var (_, map) = Expand(raw);
        return map[Math.Clamp(column, 0, raw.Length)];
    }

    /// <summary>Position du document sous un point du contrôle.</summary>
    private TextPosition PositionAt(Point point)
    {
        if (_editor is null || _documentId is not { } id) return new TextPosition(0, 0);

        int lineCount = _editor.GetLineCount(id);
        int line = Math.Clamp((int)Math.Floor((point.Y + _scrollY) / _lineHeight), 0, lineCount - 1);
        double x = point.X - TextLeft(lineCount) + _scrollX;
        int visual = Math.Max(0, (int)Math.Round(x / _charWidth));

        var raw = _editor.GetLineText(id, line);
        var (_, map) = Expand(raw);
        int column = raw.Length;
        for (int i = 0; i <= raw.Length; i++)
        {
            if (map[i] >= visual) { column = i; break; }
        }
        return new TextPosition(line, column);
    }

    // ------------------------------------------------------------------ défilement

    private double ContentHeight(int lineCount) => (lineCount + 2) * _lineHeight;

    private void ClampScroll()
    {
        int lineCount = Math.Max(1, LineCount());
        double maxY = Math.Max(0, ContentHeight(lineCount) - Bounds.Height);
        double maxX = Math.Max(0, _contentWidth + 40 - (Bounds.Width - TextLeft(lineCount) - ScrollbarWidth));
        _scrollY = Math.Clamp(_scrollY, 0, maxY);
        _scrollX = Math.Clamp(_scrollX, 0, maxX);
    }

    private void EnsureCaretVisible()
    {
        if (_editor is null || _documentId is not { } id || Bounds.Height <= 0) return;

        var caret = _editor.GetCaretSet(id).Primary.Position;
        int lineCount = _editor.GetLineCount(id);
        double top = caret.Line * _lineHeight;
        if (top < _scrollY) _scrollY = top;
        else if (top + _lineHeight > _scrollY + Bounds.Height) _scrollY = top + _lineHeight - Bounds.Height;

        double textWidth = Bounds.Width - TextLeft(lineCount) - ScrollbarWidth;
        double x = VisualColumn(caret.Line, caret.Column) * _charWidth;
        if (x < _scrollX) _scrollX = Math.Max(0, x - _charWidth * 4);
        else if (x + _charWidth * 2 > _scrollX + textWidth) _scrollX = x + _charWidth * 2 - textWidth;

        _contentWidth = Math.Max(_contentWidth, x + _charWidth);
        ClampScroll();
    }

    /// <summary>Fait défiler pour montrer une ligne (base 0), par exemple pour « Aller à la ligne » ou un diagnostic.</summary>
    public void ScrollToLine(int line)
    {
        _scrollY = Math.Max(0, line * _lineHeight - Bounds.Height / 3);
        ClampScroll();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ rendu

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var area = new Rect(Bounds.Size);
        context.DrawRectangle(_background, null, area);
        if (_editor is null || _documentId is not { } id) return;

        int lineCount = _editor.GetLineCount(id);
        var carets = _editor.GetCaretSet(id).Carets;

        double gutter = GutterWidth(lineCount);
        double textLeft = TextLeft(lineCount);
        double textWidth = Math.Max(0, area.Width - textLeft - ScrollbarWidth);

        int first = Math.Clamp((int)(_scrollY / _lineHeight), 0, lineCount - 1);
        int last = Math.Clamp((int)((_scrollY + area.Height) / _lineHeight), 0, lineCount - 1);

        var caretLines = new HashSet<int>(carets.Where(c => !c.HasSelection).Select(c => c.Position.Line));
        var cache = new Dictionary<int, (string Raw, string Display, int[] Map)>();

        using (context.PushClip(new Rect(textLeft, 0, Math.Max(0, area.Width - textLeft), area.Height)))
        {
            int startVisual = Math.Max(0, (int)(_scrollX / _charWidth) - 1);
            int visibleCols = (int)(textWidth / _charWidth) + 4;

            for (int line = first; line <= last; line++)
            {
                double y = line * _lineHeight - _scrollY;
                string raw = _editor.GetLineText(id, line);
                var (display, map) = Expand(raw);
                cache[line] = (raw, display, map);

                _contentWidth = Math.Max(_contentWidth, display.Length * _charWidth);

                if (caretLines.Contains(line))
                    context.DrawRectangle(_currentLineBrush, null, new Rect(textLeft, y, textWidth + ScrollbarWidth, _lineHeight));

                DrawSelection(context, carets, line, raw.Length, map, textLeft, y);

                if (startVisual >= display.Length) continue;
                string slice = display.Substring(startVisual, Math.Min(visibleCols, display.Length - startVisual));
                var formatted = Format(slice, _foreground);

                if (_highlighter is not null)
                {
                    foreach (var token in _highlighter.GetTokens(id, line))
                    {
                        if (token.StartColumn < 0 || token.EndColumn <= token.StartColumn) continue;
                        if (!TokenBrushes.TryGetValue(token.Kind, out var brush)) continue;
                        int s = map[Math.Min(token.StartColumn, raw.Length)] - startVisual;
                        int e = map[Math.Min(token.EndColumn, raw.Length)] - startVisual;
                        s = Math.Max(0, s);
                        e = Math.Min(slice.Length, e);
                        if (e > s) formatted.SetForegroundBrush(brush, s, e - s);
                    }
                }

                context.DrawText(formatted, new Point(textLeft - _scrollX + startVisual * _charWidth, y));
            }

            // Curseurs
            if (_caretOn && IsFocused)
            {
                foreach (var caret in carets)
                {
                    int line = caret.Position.Line;
                    if (line < first || line > last || !cache.TryGetValue(line, out var entry)) continue;
                    int visual = entry.Map[Math.Clamp(caret.Position.Column, 0, entry.Raw.Length)];
                    double x = textLeft - _scrollX + visual * _charWidth;
                    context.DrawRectangle(_foreground, null, new Rect(x, line * _lineHeight - _scrollY, 2, _lineHeight));
                }
            }
        }

        // Gouttière (numéros de ligne)
        context.DrawRectangle(_background, null, new Rect(0, 0, gutter, area.Height));
        for (int line = first; line <= last; line++)
        {
            double y = line * _lineHeight - _scrollY;
            bool current = caretLines.Contains(line);
            var number = Format((line + 1).ToString(CultureInfo.InvariantCulture), current ? _foreground : _gutterForeground);
            context.DrawText(number, new Point(gutter - GutterPadding - number.Width, y));
        }

        DrawScrollbar(context, area, lineCount);
    }

    private void DrawSelection(DrawingContext context, IReadOnlyList<Caret> carets, int line, int rawLength, int[] map, double textLeft, double y)
    {
        foreach (var caret in carets)
        {
            if (!caret.HasSelection) continue;
            bool anchorFirst = Selection.Compare(caret.Anchor, caret.Position) <= 0;
            var start = anchorFirst ? caret.Anchor : caret.Position;
            var end = anchorFirst ? caret.Position : caret.Anchor;
            if (line < start.Line || line > end.Line) continue;

            int from = line == start.Line ? start.Column : 0;
            int to = line == end.Line ? end.Column : rawLength;
            int visualFrom = map[Math.Clamp(from, 0, rawLength)];
            int visualTo = map[Math.Clamp(to, 0, rawLength)] + (line < end.Line ? 1 : 0); // +1 : le saut de ligne

            double x = textLeft - _scrollX + visualFrom * _charWidth;
            double width = Math.Max(2, (visualTo - visualFrom) * _charWidth);
            context.DrawRectangle(_selectionBrush, null, new Rect(x, y, width, _lineHeight));
        }
    }

    private void DrawScrollbar(DrawingContext context, Rect area, int lineCount)
    {
        double content = ContentHeight(lineCount);
        if (content <= area.Height)
        {
            _scrollThumb = default;
            return;
        }

        double thumbHeight = Math.Max(24, area.Height * area.Height / content);
        double travel = area.Height - thumbHeight;
        double thumbY = _scrollY / (content - area.Height) * travel;
        _scrollThumb = new Rect(area.Width - ScrollbarWidth, thumbY, ScrollbarWidth, thumbHeight);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 150, 150, 150)), null, _scrollThumb);
    }

    // ------------------------------------------------------------------ souris

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_commands is null || _documentId is not { } id) return;

        var point = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (_scrollThumb.Width > 0 && point.X >= Bounds.Width - ScrollbarWidth)
        {
            _draggingThumb = true;
            _thumbGrabOffset = _scrollThumb.Contains(point) ? point.Y - _scrollThumb.Y : _scrollThumb.Height / 2;
            DragThumb(point.Y);
            e.Pointer.Capture(this);
            return;
        }

        var position = PositionAt(point);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        Run(() =>
        {
            if (e.ClickCount >= 3) _commands.SelectLine(id, position);
            else if (e.ClickCount == 2) _commands.SelectWord(id, position);
            else if (alt) _commands.AddCaret(id, position);
            else _commands.SetCaret(id, position, extendSelection: shift);
        });

        _draggingText = e.ClickCount == 1 && !alt;
        if (_draggingText) e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_draggingThumb)
        {
            DragThumb(point.Y);
            return;
        }

        if (!_draggingText || _commands is null || _documentId is not { } id) return;

        // Sélection à la souris : on étend depuis l'ancre ; le défilement suit quand on sort du contrôle.
        if (point.Y < 0) _scrollY -= _lineHeight; else if (point.Y > Bounds.Height) _scrollY += _lineHeight;
        ClampScroll();
        var position = PositionAt(new Point(point.X, Math.Clamp(point.Y, 0, Bounds.Height)));
        Run(() => _commands.SetCaret(id, position, extendSelection: true), ensureVisible: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _draggingText = false;
        _draggingThumb = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _scrollX -= e.Delta.Y * _charWidth * 6;
        else _scrollY -= e.Delta.Y * _lineHeight * 3;
        _scrollX -= e.Delta.X * _charWidth * 6;
        ClampScroll();
        InvalidateVisual();
        e.Handled = true;
    }

    private void DragThumb(double pointerY)
    {
        int lineCount = Math.Max(1, LineCount());
        double content = ContentHeight(lineCount);
        double thumbHeight = _scrollThumb.Height;
        double travel = Bounds.Height - thumbHeight;
        if (travel <= 0) return;
        double top = Math.Clamp(pointerY - _thumbGrabOffset, 0, travel);
        _scrollY = top / travel * (content - Bounds.Height);
        ClampScroll();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ clavier

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_commands is null || _documentId is not { } id || string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.All(char.IsControl)) return; // Entrée, Tab et Échap sont traités dans OnKeyDown

        Run(() => _commands.TypeText(id, e.Text));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_commands is null || _editor is null || _documentId is not { } id) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        int page = Math.Max(1, (int)(Bounds.Height / _lineHeight) - 1);

        void Move(CaretMove move) => Run(() => _commands.MoveCarets(id, move, shift, page));

        bool handled = true;
        switch (e.Key)
        {
            case Key.Left: Move(ctrl ? CaretMove.WordLeft : CaretMove.Left); break;
            case Key.Right: Move(ctrl ? CaretMove.WordRight : CaretMove.Right); break;

            case Key.Up:
                if (ctrl && alt) Run(() => _commands.AddCaretAbove(id));
                else if (alt && shift) Run(() => _commands.DuplicateLines(id));
                else if (alt) Run(() => _commands.MoveLinesUp(id));
                else Move(CaretMove.Up);
                break;
            case Key.Down:
                if (ctrl && alt) Run(() => _commands.AddCaretBelow(id));
                else if (alt && shift) Run(() => _commands.DuplicateLines(id));
                else if (alt) Run(() => _commands.MoveLinesDown(id));
                else Move(CaretMove.Down);
                break;

            case Key.Home: Move(ctrl ? CaretMove.DocumentStart : CaretMove.LineStart); break;
            case Key.End: Move(ctrl ? CaretMove.DocumentEnd : CaretMove.LineEnd); break;
            case Key.PageUp: Move(CaretMove.PageUp); break;
            case Key.PageDown: Move(CaretMove.PageDown); break;

            case Key.Back: Run(() => _commands.Backspace(id)); break;
            case Key.Delete: Run(() => _commands.DeleteForward(id)); break;
            case Key.Enter: Run(() => _commands.InsertNewLine(id)); break;
            case Key.Tab:
                if (shift) Run(() => _commands.Outdent(id)); else Run(() => _commands.Indent(id));
                break;

            case Key.Escape: Run(() => CollapseSelection(id)); break;

            case Key.A when ctrl: Run(() => _commands.SelectAll(id)); break;
            case Key.Z when ctrl: Run(() => { if (shift) _editor.Redo(id); else _editor.Undo(id); }); break;
            case Key.Y when ctrl: Run(() => _editor.Redo(id)); break;
            case Key.C when ctrl: _ = CopyAsync(cut: false); break;
            case Key.X when ctrl: _ = CopyAsync(cut: true); break;
            case Key.V when ctrl: _ = PasteAsync(); break;
            case Key.S when ctrl: SaveRequested?.Invoke(this, EventArgs.Empty); break;

            // Ctrl+/ (touche « : / » selon le clavier) : commenter ou décommenter les lignes.
            case Key.OemQuestion when ctrl:
            case Key.Divide when ctrl:
                Run(() => ToggleComment(id));
                break;

            default: handled = false; break;
        }

        e.Handled = handled;
    }

    private void CollapseSelection(DocumentId id)
    {
        var carets = _editor!.GetCaretSet(id);
        var primary = carets.Primary;
        _editor.SetCaretSet(id, CaretSet.Single(primary.Position));
    }

    private void ToggleComment(DocumentId id)
    {
        var info = _editor!.GetInfo(id);
        string prefix = info.LanguageId switch
        {
            "python" or "shellscript" or "powershell" or "yaml" => "#",
            "sql" => "--",
            _ => "//"
        };

        int line = _editor.GetCaretSet(id).Primary.Position.Line;
        bool commented = _editor.GetLineText(id, line).TrimStart().StartsWith(prefix, StringComparison.Ordinal);
        if (commented) _commands!.UncommentLines(id, prefix);
        else _commands!.CommentLines(id, prefix);
    }

    // ------------------------------------------------------------------ presse-papiers
    // Si la version d'Avalonia change l'API du presse-papiers, seules ces deux méthodes sont à adapter.

    public async Task CopyAsync(bool cut)
    {
        if (_commands is null || _documentId is not { } id) return;
        try
        {
            string text = cut ? _commands.Cut(id) : _commands.Copy(id);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
            AfterEdit();
        }
        catch (AppException ex)
        {
            EditRejected?.Invoke(this, ex.Message);
        }
    }

    public async Task PasteAsync()
    {
        if (_commands is null || _documentId is not { } id) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            string? text = null;
            if (clipboard is not null)
            {
                using var data = await clipboard.TryGetDataAsync();
                if (data is not null) text = await data.TryGetTextAsync();
            }
            if (string.IsNullOrEmpty(text)) return;
            _commands.Paste(id, text);
            AfterEdit();
        }
        catch (AppException ex)
        {
            EditRejected?.Invoke(this, ex.Message);
        }
    }

    // ------------------------------------------------------------------ outils

    /// <summary>Exécute une commande, puis fait suivre le curseur, redessine et prévient la barre d'état.</summary>
    private void Run(Action command, bool ensureVisible = true)
    {
        try
        {
            command();
        }
        catch (AppException ex)
        {
            EditRejected?.Invoke(this, ex.Message);
        }
        AfterEdit(ensureVisible);
    }

    private void AfterEdit(bool ensureVisible = true)
    {
        ResetBlink();
        if (ensureVisible) EnsureCaretVisible();
        else ClampScroll();
        InvalidateVisual();
        CaretChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetBlink()
    {
        _caretOn = true;
        if (_blinkTimer.IsEnabled) { _blinkTimer.Stop(); _blinkTimer.Start(); }
    }
}
