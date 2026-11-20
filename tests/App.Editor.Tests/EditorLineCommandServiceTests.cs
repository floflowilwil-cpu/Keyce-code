using App.Core.Models;
using App.Editor.Editing;

namespace App.Editor.Tests;

public sealed class EditorLineCommandServiceTests
{
    private const string FilePath = "/tmp/keyce-tests/line-commands.cs";

    private static async Task<(EditorService Editor, DocumentId Id, EditorLineCommandService Commands)> Create(
        string text, params Caret[] carets)
    {
        var files = new FakeFileService();
        files.Files[Path.GetFullPath(FilePath)] = text;
        var editor = new EditorService(files);
        var info = await editor.OpenDocumentAsync(FilePath);
        editor.SetCaretSet(info.Id, new CaretSet(
            carets.Length == 0 ? [Caret.At(new TextPosition(0, 0))] : carets));
        return (editor, info.Id, new EditorLineCommandService(editor));
    }

    [Fact]
    public async Task CommentLines_AddsPrefix_AndUndoRestoresText()
    {
        var (editor, id, commands) = await Create("alpha\nbeta",
            Caret.FromSelection(new Selection(new TextPosition(0, 0), new TextPosition(1, 4))));

        commands.CommentLines(id, "//");

        Assert.Equal("//alpha\n//beta", editor.GetText(id));
        editor.Undo(id);
        Assert.Equal("alpha\nbeta", editor.GetText(id));
    }

    [Fact]
    public async Task UncommentLines_RemovesOnlyExistingPrefix()
    {
        var (editor, id, commands) = await Create("//alpha\nbeta",
            Caret.At(new TextPosition(0, 2)),
            Caret.At(new TextPosition(1, 1)));

        commands.UncommentLines(id, "//");

        Assert.Equal("alpha\nbeta", editor.GetText(id));
    }

    [Fact]
    public async Task DuplicateLines_DuplicatesTargetLine()
    {
        var (editor, id, commands) = await Create("alpha\nbeta", Caret.At(new TextPosition(1, 2)));

        commands.DuplicateLines(id);

        Assert.Equal("alpha\nbeta\nbeta", editor.GetText(id));
    }

    [Fact]
    public async Task MoveLinesUp_SwapsWithPreviousLine()
    {
        var (editor, id, commands) = await Create("alpha\nbeta\ngamma", Caret.At(new TextPosition(1, 2)));

        commands.MoveLinesUp(id);

        Assert.Equal("beta\nalpha\ngamma", editor.GetText(id));
        editor.Undo(id);
        Assert.Equal("alpha\nbeta\ngamma", editor.GetText(id));
    }

    [Fact]
    public async Task MoveLinesDown_SwapsWithNextLine()
    {
        var (editor, id, commands) = await Create("alpha\nbeta\ngamma", Caret.At(new TextPosition(1, 2)));

        commands.MoveLinesDown(id);

        Assert.Equal("alpha\ngamma\nbeta", editor.GetText(id));
    }
}
