using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Editor.Tests;

internal sealed class FakeFileService : IFileService
{
    public Dictionary<string, string> Files { get; } = new();
    public int Reads { get; private set; }

    public Task<string> ReadAsync(string path, CancellationToken ct = default)
    {
        Reads++;
        return Files.TryGetValue(path, out var c)
            ? Task.FromResult(c)
            : throw new AppException(ErrorCode.NotFound, path);
    }

    public Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        Files[path] = content;
        return Task.CompletedTask;
    }

    public Task CreateAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public IDisposable WatchChanges(string path, Action<FileChange> onChange) => new Noop();

    private sealed class Noop : IDisposable { public void Dispose() { } }
}

public class EditorServiceTests
{
    private static readonly string Path1 = System.IO.Path.GetFullPath("/tmp/keyce-tests/a.cs");

    private static (EditorService svc, FakeFileService files) Create(string content = "abc\ndef")
    {
        var files = new FakeFileService();
        files.Files[Path1] = content;
        return (new EditorService(files), files);
    }

    private static TextEdit Insert(int line, int col, string text) =>
        new(new TextRange(new TextPosition(line, col), new TextPosition(line, col)), text);

    [Fact]
    public async Task OpenDocument_ReturnsInfo_AndText()
    {
        var (svc, _) = Create();
        var info = await svc.OpenDocumentAsync(Path1);
        Assert.Equal("csharp", info.LanguageId);
        Assert.False(info.IsDirty);
        Assert.Equal("abc\ndef", svc.GetText(info.Id));
    }

    [Fact]
    public async Task OpenSamePathTwice_ReturnsSameDocument_WithoutRereading()
    {
        var (svc, files) = Create();
        var a = await svc.OpenDocumentAsync(Path1);
        var b = await svc.OpenDocumentAsync(Path1);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, files.Reads);
    }

    [Fact]
    public async Task OpenMissingFile_PropagatesNotFound()
    {
        var (svc, _) = Create();
        var ex = await Assert.ThrowsAsync<AppException>(() => svc.OpenDocumentAsync("/tmp/keyce-tests/nope.txt"));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task ApplyEdit_RaisesDocumentChanged_WithTheEdit()
    {
        var (svc, _) = Create();
        var info = await svc.OpenDocumentAsync(Path1);
        DocumentChangedEventArgs? args = null;
        svc.DocumentChanged += (_, e) => args = e;

        svc.ApplyEdit(info.Id, Insert(0, 3, "!"));

        Assert.NotNull(args);
        Assert.Equal(info.Id, args!.Id);
        Assert.Single(args.Edits);
        Assert.Equal("!", args.Edits[0].NewText);
        Assert.Equal("abc!\ndef", svc.GetText(info.Id));
    }

    [Fact]
    public async Task UndoRedo_RaiseEvents_AndRestoreText()
    {
        var (svc, _) = Create();
        var info = await svc.OpenDocumentAsync(Path1);
        int events = 0;
        svc.DocumentChanged += (_, _) => events++;

        svc.ApplyEdit(info.Id, Insert(0, 0, "X"));
        svc.Undo(info.Id);
        Assert.Equal("abc\ndef", svc.GetText(info.Id));
        svc.Redo(info.Id);
        Assert.Equal("Xabc\ndef", svc.GetText(info.Id));
        Assert.Equal(3, events);
    }

    [Fact]
    public async Task UndoWithNothingToUndo_DoesNotRaise()
    {
        var (svc, _) = Create();
        var info = await svc.OpenDocumentAsync(Path1);
        int events = 0;
        svc.DocumentChanged += (_, _) => events++;
        svc.Undo(info.Id);
        svc.Redo(info.Id);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Save_WritesThroughFileService_AndClearsDirty()
    {
        var (svc, files) = Create();
        var info = await svc.OpenDocumentAsync(Path1);
        var states = new List<bool>();
        svc.DocumentStateChanged += (_, i) => states.Add(i.IsDirty);

        svc.ApplyEdit(info.Id, Insert(1, 3, "g"));
        Assert.True(svc.GetInfo(info.Id).IsDirty);
        await svc.SaveAsync(info.Id);

        Assert.Equal("abc\ndefg", files.Files[Path1]);
        Assert.False(svc.GetInfo(info.Id).IsDirty);
        Assert.Equal(new[] { true, false }, states);
    }

    [Fact]
    public async Task Save_KeepsCrLf()
    {
        var (svc, files) = Create("a\r\nb\r\n");
        var info = await svc.OpenDocumentAsync(Path1);
        svc.ApplyEdit(info.Id, Insert(0, 1, "x"));
        await svc.SaveAsync(info.Id);
        Assert.Equal("ax\r\nb\r\n", files.Files[Path1]);
    }

    [Fact]
    public async Task UnknownDocument_ThrowsNotFound()
    {
        var (svc, _) = Create();
        var ex = Assert.Throws<AppException>(() => svc.GetText(DocumentId.New()));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CloseDocument_ForgetsIt_AndAllowsReopen()
    {
        var (svc, files) = Create();
        var a = await svc.OpenDocumentAsync(Path1);
        Assert.True(svc.CloseDocument(a.Id));
        Assert.False(svc.CloseDocument(a.Id));
        var b = await svc.OpenDocumentAsync(Path1);
        Assert.True(a.Id != b.Id);
        Assert.Equal(2, files.Reads);
    }
}
