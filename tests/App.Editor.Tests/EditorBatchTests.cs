using App.Core.Models;

namespace App.Editor.Tests;

public sealed class EditorBatchTests
{
    private static TextEdit Insert(int line, int column, string value) =>
        new(new TextRange(new TextPosition(line, column), new TextPosition(line, column)), value);

    [Fact]
    public async Task ApplyEdits_AppliesAllEditsAndOneUndoRestoresOriginal()
    {
        var files = new FakeFileService();
        string path = Path.GetFullPath("/tmp/keyce-tests/multi.txt");
        files.Files[path] = "abcdef";
        var service = new EditorService(files);
        var info = await service.OpenDocumentAsync(path);

        service.ApplyEdits(info.Id, [Insert(0, 1, "X"), Insert(0, 4, "Y")]);

        Assert.Equal("aXbcdYef", service.GetText(info.Id));
        service.Undo(info.Id);
        Assert.Equal("abcdef", service.GetText(info.Id));
        Assert.False(service.GetInfo(info.Id).IsDirty);
    }

    [Fact]
    public async Task ApplyEdits_RedoRestoresWholeBatch()
    {
        var files = new FakeFileService();
        string path = Path.GetFullPath("/tmp/keyce-tests/multi-redo.txt");
        files.Files[path] = "abcd";
        var service = new EditorService(files);
        var info = await service.OpenDocumentAsync(path);

        service.ApplyEdits(info.Id, [Insert(0, 0, "1"), Insert(0, 4, "2")]);
        service.Undo(info.Id);
        service.Redo(info.Id);

        Assert.Equal("1abcd2", service.GetText(info.Id));
    }

    [Fact]
    public async Task ApplyEdits_RejectsOverlappingRangesWithoutChangingDocument()
    {
        var files = new FakeFileService();
        string path = Path.GetFullPath("/tmp/keyce-tests/multi-overlap.txt");
        files.Files[path] = "abcdef";
        var service = new EditorService(files);
        var info = await service.OpenDocumentAsync(path);

        var overlapping = new[]
        {
            new TextEdit(new TextRange(new TextPosition(0, 1), new TextPosition(0, 4)), "X"),
            new TextEdit(new TextRange(new TextPosition(0, 3), new TextPosition(0, 5)), "Y")
        };

        Assert.Throws<App.Core.Errors.AppException>(() => service.ApplyEdits(info.Id, overlapping));
        Assert.Equal("abcdef", service.GetText(info.Id));
    }

    [Fact]
    public async Task CaretSet_IsStoredPerDocumentAndPositionsAreValidated()
    {
        var files = new FakeFileService();
        string path = Path.GetFullPath("/tmp/keyce-tests/carets.txt");
        files.Files[path] = "abc\ndef";
        var service = new EditorService(files);
        var info = await service.OpenDocumentAsync(path);
        var set = new CaretSet([
            Caret.At(new TextPosition(0, 1)),
            Caret.At(new TextPosition(1, 2))
        ], primaryIndex: 1);

        service.SetCaretSet(info.Id, set);

        Assert.Equal(set, service.GetCaretSet(info.Id));
        Assert.Throws<App.Core.Errors.AppException>(() =>
            service.SetCaretSet(info.Id, CaretSet.Single(new TextPosition(9, 0))));
    }
}
