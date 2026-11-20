using App.Core.Models;
using App.Editor.Editing;

namespace App.Editor.Tests;

public sealed class EditorCommandServiceTests
{
    private const string FilePath = "/tmp/keyce-tests/commands.cs";

    private static async Task<(EditorService Editor, DocumentId Id, EditorCommandService Commands)> Create(
        string text,
        params Caret[] carets)
    {
        var files = new FakeFileService();
        files.Files[Path.GetFullPath(FilePath)] = text;
        var editor = new EditorService(files);
        var info = await editor.OpenDocumentAsync(FilePath);
        editor.SetCaretSet(info.Id, new CaretSet(
            carets.Length == 0 ? [Caret.At(new TextPosition(0, 0))] : carets));
        return (editor, info.Id, new EditorCommandService(editor));
    }

    [Fact]
    public async Task TypeText_ReplacesSelection_AndCollapsesCaret()
    {
        var (editor, id, commands) = await Create("hello", Caret.FromSelection(
            new Selection(new TextPosition(0, 1), new TextPosition(0, 4))));

        commands.TypeText(id, "ipp");

        Assert.Equal("hippo", editor.GetText(id));
        Assert.Equal(new TextPosition(0, 4), editor.GetCaretSet(id).Primary.Position);
        editor.Undo(id);
        Assert.Equal("hello", editor.GetText(id));
    }

    [Fact]
    public async Task TypeText_AppliesToMultipleCarets_AndUndoIsSingleStep()
    {
        var (editor, id, commands) = await Create("abcd",
            Caret.At(new TextPosition(0, 1)),
            Caret.At(new TextPosition(0, 3)));

        commands.TypeText(id, "X");

        Assert.Equal("aXbcXd", editor.GetText(id));
        editor.Undo(id);
        Assert.Equal("abcd", editor.GetText(id));
    }

    [Fact]
    public async Task Backspace_DeletesPreviousCharacterAtEachCaret()
    {
        var (editor, id, commands) = await Create("abcd",
            Caret.At(new TextPosition(0, 2)),
            Caret.At(new TextPosition(0, 4)));

        commands.Backspace(id);

        Assert.Equal("ac", editor.GetText(id));
        Assert.Equal(new TextPosition(0, 1), editor.GetCaretSet(id).Carets[0].Position);
        Assert.Equal(new TextPosition(0, 2), editor.GetCaretSet(id).Carets[1].Position);
    }

    [Fact]
    public async Task Backspace_DeletesWholeSurrogatePair()
    {
        var (editor, id, commands) = await Create("A😀B", Caret.At(new TextPosition(0, 3)));

        commands.Backspace(id);

        Assert.Equal("AB", editor.GetText(id));
    }

    [Fact]
    public async Task DeleteForward_DeletesNextCharacter()
    {
        var (editor, id, commands) = await Create("abcd", Caret.At(new TextPosition(0, 1)));

        commands.DeleteForward(id);

        Assert.Equal("acd", editor.GetText(id));
        Assert.Equal(new TextPosition(0, 1), editor.GetCaretSet(id).Primary.Position);
    }

    [Fact]
    public async Task InsertNewLine_PreservesIndentation()
    {
        var (editor, id, commands) = await Create("    value", Caret.At(new TextPosition(0, 9)));

        commands.InsertNewLine(id);

        Assert.Equal("    value\n    ", editor.GetText(id));
        Assert.Equal(new TextPosition(1, 4), editor.GetCaretSet(id).Primary.Position);
    }

    [Fact]
    public async Task Indent_InsertsTabAtMultipleCarets()
    {
        var (editor, id, commands) = await Create("ab\ncd",
            Caret.At(new TextPosition(0, 1)),
            Caret.At(new TextPosition(1, 1)));

        commands.Indent(id);

        Assert.Equal("a\tb\nc\td", editor.GetText(id));
    }

    [Fact]
    public async Task Outdent_RemovesLeadingIndentationOnSelectedLines()
    {
        var (editor, id, commands) = await Create("    alpha\n\tbeta",
            Caret.At(new TextPosition(0, 5)),
            Caret.At(new TextPosition(1, 2)));

        commands.Outdent(id);

        Assert.Equal("alpha\nbeta", editor.GetText(id));
    }

    [Fact]
    public async Task BackspaceAtDocumentStart_DoesNotChangeText()
    {
        var (editor, id, commands) = await Create("abc", Caret.At(new TextPosition(0, 0)));

        commands.Backspace(id);

        Assert.Equal("abc", editor.GetText(id));
    }
}
