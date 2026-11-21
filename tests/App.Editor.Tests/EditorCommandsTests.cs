using App.Core.Contracts;
using App.Core.Models;
using App.Editor.Editing;

namespace App.Editor.Tests;

/// <summary>Tests du contrat IEditorCommands : presse-papiers, sélection, recherche et délégation.</summary>
public sealed class EditorCommandsTests
{
    private const string FilePath = "/tmp/keyce-tests/commands.cs";

    private static TextPosition At(int line, int column) => new(line, column);

    private static Caret Select(int l1, int c1, int l2, int c2) =>
        Caret.FromSelection(new Selection(At(l1, c1), At(l2, c2)));

    private static async Task<(EditorService Editor, DocumentId Id, IEditorCommands Commands)> Create(
        string text, params Caret[] carets)
    {
        var files = new FakeFileService();
        files.Files[Path.GetFullPath(FilePath)] = text;
        var editor = new EditorService(files);
        var info = await editor.OpenDocumentAsync(FilePath);
        editor.SetCaretSet(info.Id, new CaretSet(
            carets.Length == 0 ? [Caret.At(At(0, 0))] : carets));
        return (editor, info.Id, new EditorCommands(editor));
    }

    // ---------- Délégation ----------

    [Fact]
    public async Task TypeText_GoesThroughTheContract()
    {
        var (editor, id, commands) = await Create("ac", Caret.At(At(0, 1)));
        commands.TypeText(id, "b");
        Assert.Equal("abc", editor.GetText(id));
    }

    // ---------- SelectAll ----------

    [Fact]
    public async Task SelectAll_SelectsWholeDocument_WithASingleCaret()
    {
        var (editor, id, commands) = await Create("ab\ncd", Caret.At(At(1, 1)), Caret.At(At(0, 1)));

        commands.SelectAll(id);

        var carets = editor.GetCaretSet(id);
        Assert.Equal(1, carets.Count);
        Assert.Equal(At(0, 0), carets.Primary.Anchor);
        Assert.Equal(At(1, 2), carets.Primary.Position);
    }

    // ---------- Copy ----------

    [Fact]
    public async Task Copy_ReturnsSelectedText_AndDoesNotModifyDocument()
    {
        var (editor, id, commands) = await Create("hello world", Select(0, 0, 0, 5));

        Assert.Equal("hello", commands.Copy(id));
        Assert.Equal("hello world", editor.GetText(id));
    }

    [Fact]
    public async Task Copy_WithoutSelection_ReturnsTheWholeLine_WithNewLine()
    {
        var (_, id, commands) = await Create("ab\ncd", Caret.At(At(1, 1)));
        Assert.Equal("cd\n", commands.Copy(id));
    }

    [Fact]
    public async Task Copy_WithSeveralSelections_JoinsThemWithNewLine()
    {
        var (_, id, commands) = await Create("abc def", Select(0, 0, 0, 3), Select(0, 4, 0, 7));
        Assert.Equal("abc\ndef", commands.Copy(id));
    }

    // ---------- Cut ----------

    [Fact]
    public async Task Cut_Selection_RemovesItAndReturnsIt_ThenUndoRestores()
    {
        var (editor, id, commands) = await Create("hello world", Select(0, 0, 0, 6));

        string cut = commands.Cut(id);

        Assert.Equal("hello ", cut);
        Assert.Equal("world", editor.GetText(id));
        Assert.Equal(At(0, 0), editor.GetCaretSet(id).Primary.Position);

        editor.Undo(id);
        Assert.Equal("hello world", editor.GetText(id));
    }

    [Fact]
    public async Task Cut_WithoutSelection_RemovesTheWholeLine()
    {
        var (editor, id, commands) = await Create("a\nb\nc", Caret.At(At(1, 0)));

        string cut = commands.Cut(id);

        Assert.Equal("b\n", cut);
        Assert.Equal("a\nc", editor.GetText(id));
        Assert.Equal(At(1, 0), editor.GetCaretSet(id).Primary.Position);
    }

    [Fact]
    public async Task Cut_LastLine_AlsoRemovesThePrecedingNewLine()
    {
        var (editor, id, commands) = await Create("a\nb\nc", Caret.At(At(2, 0)));

        string cut = commands.Cut(id);

        Assert.Equal("c\n", cut);
        Assert.Equal("a\nb", editor.GetText(id));
        Assert.Equal(At(1, 1), editor.GetCaretSet(id).Primary.Position);
    }

    [Fact]
    public async Task Cut_TwoLastLines_RemovesBothWithoutOverlap()
    {
        var (editor, id, commands) = await Create("a\nb\nc", Caret.At(At(1, 0)), Caret.At(At(2, 0)));

        commands.Cut(id);

        Assert.Equal("a", editor.GetText(id));
    }

    [Fact]
    public async Task Cut_OnlyLine_EmptiesTheDocument()
    {
        var (editor, id, commands) = await Create("abc", Caret.At(At(0, 1)));

        string cut = commands.Cut(id);

        Assert.Equal("abc\n", cut);
        Assert.Equal("", editor.GetText(id));
    }

    // ---------- Paste ----------

    [Fact]
    public async Task Paste_InsertsAtCaret_AndMovesCaretAfterIt()
    {
        var (editor, id, commands) = await Create("ac", Caret.At(At(0, 1)));

        commands.Paste(id, "b");

        Assert.Equal("abc", editor.GetText(id));
        Assert.Equal(At(0, 2), editor.GetCaretSet(id).Primary.Position);
    }

    [Fact]
    public async Task Paste_ReplacesSelection()
    {
        var (editor, id, commands) = await Create("hello world", Select(0, 6, 0, 11));

        commands.Paste(id, "there");

        Assert.Equal("hello there", editor.GetText(id));
    }

    [Fact]
    public async Task Paste_NormalizesCrLf()
    {
        var (editor, id, commands) = await Create("", Caret.At(At(0, 0)));

        commands.Paste(id, "a\r\nb");

        Assert.Equal("a\nb", editor.GetText(id));
        Assert.Equal(At(1, 1), editor.GetCaretSet(id).Primary.Position);
    }

    [Fact]
    public async Task Paste_WithAsManyLinesAsCarets_GivesEachCaretItsLine()
    {
        var (editor, id, commands) = await Create("x\ny", Caret.At(At(0, 1)), Caret.At(At(1, 1)));

        commands.Paste(id, "A\nB");

        Assert.Equal("xA\nyB", editor.GetText(id));
        var carets = editor.GetCaretSet(id);
        Assert.Equal(At(0, 2), carets.Carets[0].Position);
        Assert.Equal(At(1, 2), carets.Carets[1].Position);
    }

    [Fact]
    public async Task Paste_WithDifferentLineCount_GivesEachCaretTheWholeText()
    {
        var (editor, id, commands) = await Create("x\ny", Caret.At(At(0, 1)), Caret.At(At(1, 1)));

        commands.Paste(id, "A\nB\nC");

        Assert.Equal("xA\nB\nC\nyA\nB\nC", editor.GetText(id));
    }

    [Fact]
    public async Task Paste_IsOneUndoStep()
    {
        var (editor, id, commands) = await Create("x\ny", Caret.At(At(0, 1)), Caret.At(At(1, 1)));

        commands.Paste(id, "A\nB");
        editor.Undo(id);

        Assert.Equal("x\ny", editor.GetText(id));
    }

    // ---------- Recherche ----------

    [Fact]
    public async Task FindAll_ReturnsRanges()
    {
        var (_, id, commands) = await Create("foo bar foo");

        var ranges = commands.FindAll(id, "foo");

        Assert.Equal(2, ranges.Count);
        Assert.Equal(new TextRange(At(0, 0), At(0, 3)), ranges[0]);
        Assert.Equal(new TextRange(At(0, 8), At(0, 11)), ranges[1]);
    }

    [Fact]
    public async Task FindNext_AndFindPrevious_UseTheGivenPosition()
    {
        var (_, id, commands) = await Create("foo bar foo");

        Assert.Equal(new TextRange(At(0, 8), At(0, 11)), commands.FindNext(id, "foo", At(0, 1)));
        Assert.Equal(new TextRange(At(0, 0), At(0, 3)), commands.FindPrevious(id, "foo", At(0, 8)));
    }

    [Fact]
    public async Task FindNext_WithoutMatch_ReturnsNull()
    {
        var (_, id, commands) = await Create("foo");
        Assert.Null(commands.FindNext(id, "zzz", At(0, 0)));
    }

    [Fact]
    public async Task ReplaceAll_ReplacesEverything_InASingleUndoStep()
    {
        var (editor, id, commands) = await Create("foo bar foo");

        int count = commands.ReplaceAll(id, "foo", "x");

        Assert.Equal(2, count);
        Assert.Equal("x bar x", editor.GetText(id));
        editor.Undo(id);
        Assert.Equal("foo bar foo", editor.GetText(id));
    }

    [Fact]
    public async Task ReplaceAll_WithoutMatch_ReturnsZero_AndKeepsDocumentClean()
    {
        var (editor, id, commands) = await Create("foo");

        Assert.Equal(0, commands.ReplaceAll(id, "zzz", "x"));
        Assert.False(editor.GetInfo(id).IsDirty);
    }

    [Fact]
    public async Task ReplaceNext_ReplacesOne_AndSelectsTheNextMatch()
    {
        var (editor, id, commands) = await Create("foo foo foo", Caret.At(At(0, 0)));

        bool replaced = commands.ReplaceNext(id, "foo", "x");

        Assert.True(replaced);
        Assert.Equal("x foo foo", editor.GetText(id));
        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(0, 2), caret.Anchor);
        Assert.Equal(At(0, 5), caret.Position);
    }

    [Fact]
    public async Task ReplaceNext_OnLastMatch_PlacesCaretAfterReplacement()
    {
        var (editor, id, commands) = await Create("foo", Caret.At(At(0, 0)));

        Assert.True(commands.ReplaceNext(id, "foo", "bar", new SearchOptions(WrapAround: false)));

        Assert.Equal("bar", editor.GetText(id));
        Assert.Equal(At(0, 3), editor.GetCaretSet(id).Primary.Position);
        Assert.False(commands.ReplaceNext(id, "foo", "x", new SearchOptions(WrapAround: false)));
    }
}
