using App.Core.Contracts;
using App.Core.Models;
using App.Editor.Editing;

namespace App.Editor.Tests;

/// <summary>Tests de non-régression : indentation multi-lignes, déplacement de blocs, commentaires,
/// regex par ligne, curseurs après Undo/Redo et absence de copie du document à chaque commande.</summary>
public sealed class EditorCommandFixesTests
{
    private const string FilePath = "/tmp/keyce-tests/fixes.cs";

    private static TextPosition At(int line, int column) => new(line, column);

    /// <summary>Sélection : ancre (l1,c1) puis extrémité active (l2,c2).</summary>
    private static Caret Sel(int l1, int c1, int l2, int c2) =>
        Caret.FromSelection(new Selection(At(l1, c1), At(l2, c2)));

    private static async Task<(EditorService Editor, DocumentId Id)> Open(string text, params Caret[] carets)
    {
        var files = new FakeFileService();
        files.Files[Path.GetFullPath(FilePath)] = text;
        var editor = new EditorService(files);
        var info = await editor.OpenDocumentAsync(FilePath);
        if (carets.Length > 0) editor.SetCaretSet(info.Id, new CaretSet(carets));
        return (editor, info.Id);
    }

    // ---------- Indent ----------

    [Fact]
    public async Task Indent_MultiLineSelection_IndentsEachLine_InOneUndoStep()
    {
        var (editor, id) = await Open("one\ntwo\nthree", Sel(0, 0, 1, 3));

        new EditorCommandService(editor).Indent(id);

        Assert.Equal("\tone\n\ttwo\nthree", editor.GetText(id));
        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(0, 0), caret.Anchor);
        Assert.Equal(At(1, 4), caret.Position);
        editor.Undo(id);
        Assert.Equal("one\ntwo\nthree", editor.GetText(id));
    }

    [Fact]
    public async Task Indent_SelectionEndingAtColumnZero_DoesNotIndentThatLastLine()
    {
        var (editor, id) = await Open("a\nb\nc", Sel(0, 0, 2, 0));
        new EditorCommandService(editor).Indent(id);
        Assert.Equal("\ta\n\tb\nc", editor.GetText(id));
    }

    [Fact]
    public async Task Indent_MultiLineSelection_SkipsEmptyLines()
    {
        var (editor, id) = await Open("a\n\nb", Sel(0, 0, 2, 1));
        new EditorCommandService(editor).Indent(id);
        Assert.Equal("\ta\n\n\tb", editor.GetText(id));
    }

    [Fact]
    public async Task Indent_SelectionOnOneLine_StillReplacesTheSelection()
    {
        var (editor, id) = await Open("hello", Sel(0, 1, 0, 4));
        new EditorCommandService(editor).Indent(id);
        Assert.Equal("h\to", editor.GetText(id));
    }

    // ---------- Outdent ----------

    [Fact]
    public async Task Outdent_MultiLineSelection_OutdentsEveryLineOfTheSelection()
    {
        var (editor, id) = await Open("\ta\n\tb\n\tc", Sel(0, 1, 1, 1));

        new EditorCommandService(editor).Outdent(id);

        Assert.Equal("a\nb\n\tc", editor.GetText(id));
        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(0, 0), caret.Anchor);
        Assert.Equal(At(1, 0), caret.Position);
    }

    [Fact]
    public async Task Outdent_Spaces_CaretFollowsTheText()
    {
        var (editor, id) = await Open("    x", Caret.At(At(0, 5)));
        new EditorCommandService(editor).Outdent(id);
        Assert.Equal("x", editor.GetText(id));
        Assert.Equal(At(0, 1), editor.GetCaretSet(id).Primary.Position);
    }

    // ---------- MoveLines ----------

    [Fact]
    public async Task MoveLinesUp_Block_MovesAllSelectedLinesTogether()
    {
        var (editor, id) = await Open("a\nb\nc\nd", Sel(2, 0, 3, 1));

        new EditorLineCommandService(editor).MoveLinesUp(id);

        Assert.Equal("a\nc\nd\nb", editor.GetText(id));
        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(1, 0), caret.Anchor);
        Assert.Equal(At(2, 1), caret.Position);
        editor.Undo(id);
        Assert.Equal("a\nb\nc\nd", editor.GetText(id));
    }

    [Fact]
    public async Task MoveLinesDown_Block_MovesAllSelectedLinesTogether()
    {
        var (editor, id) = await Open("a\nb\nc\nd", Sel(0, 0, 1, 1));
        new EditorLineCommandService(editor).MoveLinesDown(id);
        Assert.Equal("c\na\nb\nd", editor.GetText(id));
    }

    [Fact]
    public async Task MoveLines_AtDocumentEdges_DoNothing()
    {
        var (editor, id) = await Open("a\nb", Caret.At(At(0, 0)));
        int events = 0;
        editor.DocumentChanged += (_, _) => events++;
        var commands = new EditorLineCommandService(editor);

        commands.MoveLinesUp(id);
        editor.SetCaretSet(id, new CaretSet([Caret.At(At(1, 0))]));
        commands.MoveLinesDown(id);

        Assert.Equal("a\nb", editor.GetText(id));
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task MoveLinesUp_TwoSeparateBlocks_AreMovedIndependently()
    {
        var (editor, id) = await Open("a\nb\nc\nd\ne\nf", Caret.At(At(1, 0)), Caret.At(At(4, 0)));
        new EditorLineCommandService(editor).MoveLinesUp(id);
        Assert.Equal("b\na\nc\ne\nd\nf", editor.GetText(id));
    }

    [Fact]
    public async Task MoveLinesUp_SelectionEndingAtColumnZero_KeepsTheSameLinesSelected()
    {
        var (editor, id) = await Open("a\nb\nc\nd", Sel(1, 0, 3, 0));
        new EditorLineCommandService(editor).MoveLinesUp(id);
        Assert.Equal("b\nc\na\nd", editor.GetText(id));
        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(0, 0), caret.Anchor);
        Assert.Equal(At(2, 0), caret.Position);
    }

    [Fact]
    public async Task MoveLinesDown_SelectionEndingAtColumnZero_KeepsTheSameLinesSelected()
    {
        var (editor, id) = await Open("a\nb\nc\nd", Sel(0, 0, 2, 0));
        new EditorLineCommandService(editor).MoveLinesDown(id);
        Assert.Equal("c\na\nb\nd", editor.GetText(id));
        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(1, 0), caret.Anchor);
        Assert.Equal(At(3, 0), caret.Position);
    }

    [Fact]
    public async Task MoveLinesUp_CaretFollowsItsLine()
    {
        var (editor, id) = await Open("alpha\nbeta\ngamma", Caret.At(At(1, 2)));
        new EditorLineCommandService(editor).MoveLinesUp(id);
        Assert.Equal(At(0, 2), editor.GetCaretSet(id).Primary.Position);
    }

    [Fact]
    public async Task MoveLinesUp_OnlyRewritesTheTwoLinesInvolved()
    {
        string text = string.Join("\n", Enumerable.Range(0, 10).Select(i => "l" + i));
        var (editor, id) = await Open(text, Caret.At(At(5, 0)));
        DocumentChangedEventArgs? args = null;
        editor.DocumentChanged += (_, e) => args = e;

        new EditorLineCommandService(editor).MoveLinesUp(id);

        Assert.NotNull(args);
        Assert.Single(args!.Edits);
        Assert.Equal(4, args.Edits[0].Range.Start.Line);
        Assert.Equal(5, args.Edits[0].Range.End.Line);
    }

    // ---------- Comment / Uncomment ----------

    [Fact]
    public async Task UncommentLines_AlsoRemovesTheSpaceAfterThePrefix()
    {
        var (editor, id) = await Open("// hello\n//world", Sel(0, 0, 1, 3));
        new EditorLineCommandService(editor).UncommentLines(id);
        Assert.Equal("hello\nworld", editor.GetText(id));
    }

    [Fact]
    public async Task UncommentLines_WorksAfterIndentation_AndCustomPrefix()
    {
        var (editor, id) = await Open("    # x\n\t# y\nz", Sel(0, 0, 2, 1));
        new EditorLineCommandService(editor).UncommentLines(id, "#");
        Assert.Equal("    x\n\ty\nz", editor.GetText(id));
    }

    [Fact]
    public async Task CommentLines_PlacesThePrefixAfterTheIndentation()
    {
        var (editor, id) = await Open("    foo();\n\tbar();", Sel(0, 0, 1, 3));
        new EditorLineCommandService(editor).CommentLines(id);
        Assert.Equal("    //foo();\n\t//bar();", editor.GetText(id));
    }

    [Fact]
    public async Task CommentThenUncomment_RestoresTheOriginalText()
    {
        var (editor, id) = await Open("    foo();\nbar();", Sel(0, 0, 1, 3));
        var commands = new EditorLineCommandService(editor);
        commands.CommentLines(id);
        commands.UncommentLines(id);
        Assert.Equal("    foo();\nbar();", editor.GetText(id));
    }

    // ---------- Recherche ----------

    [Fact]
    public void Regex_AnchorsMatchAtEachLine()
    {
        var model = new TextModel("a1\nb\na2\n3a");
        var search = new SearchReplaceService();
        var regex = new SearchOptions(UseRegex: true);
        Assert.Equal(2, search.FindAll(model, "^a", regex).Count);       // début de ligne
        Assert.Equal(2, search.FindAll(model, @"\d$", regex).Count);     // fin de ligne : « a1 » et « a2 »
    }

    [Fact]
    public void FindNext_FindsMatchAtOrAfterStart_AndWraps()
    {
        var model = new TextModel("foo bar foo");
        var search = new SearchReplaceService();

        Assert.Equal(8, search.FindNext(model, "foo", 1)!.Offset);
        Assert.Equal(0, search.FindNext(model, "foo", 9)!.Offset);               // retour au début
        Assert.Null(search.FindNext(model, "foo", 9, new SearchOptions(WrapAround: false)));
        Assert.Null(search.FindNext(model, "zzz", 0));
    }

    [Fact]
    public void FindNext_WholeWord_IgnoresMatchesInsideOtherWords()
    {
        var model = new TextModel("foobar foo");
        var match = new SearchReplaceService().FindNext(model, "foo", 1, new SearchOptions(WholeWord: true));
        Assert.Equal(7, match!.Offset);
    }

    // ---------- Curseurs après Undo / Redo / modification directe ----------

    [Fact]
    public async Task Undo_PutsTheCaretWhereTheTextWasRestored_AndRedoAfterTheReinsertedText()
    {
        var (editor, id) = await Open("abc", Caret.At(At(0, 1)));
        new EditorCommandService(editor).TypeText(id, "XXXX");
        Assert.Equal(At(0, 5), editor.GetCaretSet(id).Primary.Position);

        editor.Undo(id);
        Assert.Equal(At(0, 1), editor.GetCaretSet(id).Primary.Position);

        editor.Redo(id);
        Assert.Equal(At(0, 5), editor.GetCaretSet(id).Primary.Position);
    }

    [Fact]
    public async Task UndoRedo_WithMultipleCarets_RestoresOneCaretPerEdit()
    {
        var (editor, id) = await Open("abcd", Caret.At(At(0, 1)), Caret.At(At(0, 3)));
        new EditorCommandService(editor).TypeText(id, "X");
        Assert.Equal("aXbcXd", editor.GetText(id));

        editor.Undo(id);
        Assert.Equal(new[] { At(0, 1), At(0, 3) }, editor.GetCaretSet(id).Carets.Select(c => c.Position).ToArray());

        editor.Redo(id);
        Assert.Equal(new[] { At(0, 2), At(0, 5) }, editor.GetCaretSet(id).Carets.Select(c => c.Position).ToArray());
    }

    [Fact]
    public async Task DirectEdit_KeepsCaretsInsideTheDocument()
    {
        var (editor, id) = await Open("abcdef", Caret.At(At(0, 5)), Caret.At(At(0, 6)));

        editor.ApplyEdit(id, new TextEdit(new TextRange(At(0, 0), At(0, 4)), ""));

        Assert.Equal("ef", editor.GetText(id));
        var carets = editor.GetCaretSet(id);
        Assert.Equal(1, carets.Count);                    // les deux curseurs se rejoignent
        Assert.Equal(At(0, 2), carets.Primary.Position);
    }

    // ---------- Pas de copie complète du document ----------

    private class ForwardingEditor(IEditorService inner) : IEditorService
    {
        public int GetTextCalls { get; private set; }

        public Task<DocumentInfo> OpenDocumentAsync(string path, CancellationToken ct = default) => inner.OpenDocumentAsync(path, ct);
        public string GetText(DocumentId id) { GetTextCalls++; return inner.GetText(id); }
        public void ApplyEdit(DocumentId id, TextEdit edit) => inner.ApplyEdit(id, edit);
        public void ApplyEdits(DocumentId id, IReadOnlyList<TextEdit> edits) => inner.ApplyEdits(id, edits);
        public CaretSet GetCaretSet(DocumentId id) => inner.GetCaretSet(id);
        public void SetCaretSet(DocumentId id, CaretSet carets) => inner.SetCaretSet(id, carets);
        public void Undo(DocumentId id) => inner.Undo(id);
        public void Redo(DocumentId id) => inner.Redo(id);
        public Task SaveAsync(DocumentId id, CancellationToken ct = default) => inner.SaveAsync(id, ct);
        public int GetLineCount(DocumentId id) => inner.GetLineCount(id);
        public string GetLineText(DocumentId id, int line) => inner.GetLineText(id, line);
        public FileEncoding GetEncoding(DocumentId id) => inner.GetEncoding(id);
        public void SetEncoding(DocumentId id, FileEncoding encoding) => inner.SetEncoding(id, encoding);
        public DocumentInfo CreateDocument() => inner.CreateDocument();
        public Task SaveAsAsync(DocumentId id, string path, CancellationToken ct = default) => inner.SaveAsAsync(id, path, ct);
        public bool CloseDocument(DocumentId id) => inner.CloseDocument(id);
        public DocumentInfo GetInfo(DocumentId id) => inner.GetInfo(id);
        public IReadOnlyList<DocumentInfo> GetOpenDocuments() => inner.GetOpenDocuments();
        public Task CheckExternalChangeAsync(DocumentId id, CancellationToken ct = default) => inner.CheckExternalChangeAsync(id, ct);
        public Task ReloadFromDiskAsync(DocumentId id, CancellationToken ct = default) => inner.ReloadFromDiskAsync(id, ct);
        public Task KeepLocalVersionAsync(DocumentId id, CancellationToken ct = default) => inner.KeepLocalVersionAsync(id, ct);
        public event EventHandler<DocumentChangedEventArgs> DocumentChanged
        {
            add => inner.DocumentChanged += value;
            remove => inner.DocumentChanged -= value;
        }
        public event EventHandler<DocumentInfo> DocumentOpened
        {
            add => inner.DocumentOpened += value;
            remove => inner.DocumentOpened -= value;
        }
        public event EventHandler<DocumentInfo> DocumentClosed
        {
            add => inner.DocumentClosed += value;
            remove => inner.DocumentClosed -= value;
        }
        public event EventHandler<DocumentInfo> DocumentStateChanged
        {
            add => inner.DocumentStateChanged += value;
            remove => inner.DocumentStateChanged -= value;
        }
        public event EventHandler<ExternalChangeEventArgs> ExternalChange
        {
            add => inner.ExternalChange += value;
            remove => inner.ExternalChange -= value;
        }
    }

    private sealed class ForwardingEditorWithModel(EditorService inner) : ForwardingEditor(inner), ITextModelSource
    {
        public TextModel GetTextModel(DocumentId id) => inner.GetTextModel(id);
    }

    [Fact]
    public async Task Commands_DoNotCopyTheWholeDocument_WhenTheModelIsAvailable()
    {
        var (editor, id) = await Open("a\n\tb\nc", Caret.At(At(1, 2)));
        var spy = new ForwardingEditorWithModel(editor);
        var commands = new EditorCommandService(spy);
        var lines = new EditorLineCommandService(spy);

        commands.TypeText(id, "x");
        commands.Backspace(id);
        commands.InsertNewLine(id);
        commands.Indent(id);
        commands.Outdent(id);
        lines.CommentLines(id);
        lines.MoveLinesUp(id);

        Assert.Equal(0, spy.GetTextCalls);
    }

    [Fact]
    public async Task Commands_StillWork_WhenOnlyTheBasicContractIsAvailable()
    {
        var (editor, id) = await Open("hello", Caret.At(At(0, 5)));
        var spy = new ForwardingEditor(editor);

        new EditorCommandService(spy).TypeText(id, "!");
        new EditorLineCommandService(spy).CommentLines(id);

        Assert.Equal("//hello!", editor.GetText(id));
        Assert.True(spy.GetTextCalls > 0);
    }
}
