using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Editor.Tests;

/// <summary>Faux disque qui sait renommer : un fichier, ou tout un dossier (préfixe de chemin).</summary>
internal sealed class MovableFileService : IFileService
{
    public Dictionary<string, string> Files { get; } = new();
    public HashSet<string> Existing { get; } = new();

    public Task<string> ReadAsync(string path, CancellationToken ct = default) =>
        Files.TryGetValue(path, out var c) ? Task.FromResult(c) : throw new AppException(ErrorCode.NotFound, path);

    public Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        Files[path] = content;
        return Task.CompletedTask;
    }

    public Task CreateAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public IDisposable WatchChanges(string path, Action<FileChange> onChange) => new Noop();

    public Task MoveAsync(string path, string newPath, CancellationToken ct = default)
    {
        if (Files.ContainsKey(newPath))
            throw new AppException(ErrorCode.AlreadyExists, newPath);

        string sep = System.IO.Path.DirectorySeparatorChar.ToString();
        foreach (var key in Files.Keys.ToList())
        {
            if (key == path || key.StartsWith(path + sep, StringComparison.Ordinal))
            {
                string moved = newPath + key[path.Length..];
                Files[moved] = Files[key];
                Files.Remove(key);
            }
        }
        return Task.CompletedTask;
    }

    private sealed class Noop : IDisposable { public void Dispose() { } }
}

public sealed class RenamePathTests
{
    private static string F(string rel) => System.IO.Path.GetFullPath("/tmp/keyce-tests/rename/" + rel);

    private static TextEdit Insert(int line, int col, string text) =>
        new(new TextRange(new TextPosition(line, col), new TextPosition(line, col)), text);

    private static async Task<(EditorService Svc, MovableFileService Files)> Setup(params (string Path, string Text)[] files)
    {
        var fs = new MovableFileService();
        foreach (var (path, text) in files) fs.Files[path] = text;
        return (new EditorService(fs), fs);
    }

    [Fact]
    public async Task RenameFile_OpenDocumentFollowsTheNewPath()
    {
        var (svc, fs) = await Setup((F("a.cs"), "class A {}"));
        var doc = await svc.OpenDocumentAsync(F("a.cs"));

        var moved = await svc.MovePathAsync(F("a.cs"), F("b.cs"));

        Assert.Equal(F("b.cs"), Assert.Single(moved).Path);
        Assert.Equal(F("b.cs"), svc.GetInfo(doc.Id).Path);
        Assert.True(fs.Files.ContainsKey(F("b.cs")));
        Assert.False(fs.Files.ContainsKey(F("a.cs")));
    }

    [Fact]
    public async Task Rename_KeepsUnsavedChangesAndHistory()
    {
        var (svc, _) = await Setup((F("a.cs"), "abc"));
        var doc = await svc.OpenDocumentAsync(F("a.cs"));
        svc.ApplyEdit(doc.Id, Insert(0, 0, "x"));

        await svc.MovePathAsync(F("a.cs"), F("b.cs"));

        Assert.True(svc.GetInfo(doc.Id).IsDirty);
        Assert.Equal("xabc", svc.GetText(doc.Id));
        svc.Undo(doc.Id);
        Assert.Equal("abc", svc.GetText(doc.Id));
    }

    [Fact]
    public async Task Rename_RaisesDocumentStateChanged_WithTheNewPath()
    {
        var (svc, _) = await Setup((F("a.cs"), "abc"));
        var doc = await svc.OpenDocumentAsync(F("a.cs"));
        var states = new List<DocumentInfo>();
        svc.DocumentStateChanged += (_, i) => states.Add(i);

        await svc.MovePathAsync(F("a.cs"), F("b.cs"));

        Assert.Equal(F("b.cs"), Assert.Single(states).Path);
        Assert.Equal(doc.Id, states[0].Id);
    }

    [Fact]
    public async Task Rename_ChangingTheExtension_UpdatesTheLanguage()
    {
        var (svc, _) = await Setup((F("a.cs"), "abc"));
        var doc = await svc.OpenDocumentAsync(F("a.cs"));
        Assert.Equal("csharp", doc.LanguageId);

        var moved = await svc.MovePathAsync(F("a.cs"), F("a.py"));

        Assert.Equal("python", Assert.Single(moved).LanguageId);
    }

    [Fact]
    public async Task RenameFolder_MovesEveryOpenDocumentInside()
    {
        var (svc, _) = await Setup(
            (F("src/a.cs"), "a"), (F("src/sub/b.cs"), "b"), (F("src2/c.cs"), "c"));
        var a = await svc.OpenDocumentAsync(F("src/a.cs"));
        var b = await svc.OpenDocumentAsync(F("src/sub/b.cs"));
        var c = await svc.OpenDocumentAsync(F("src2/c.cs")); // même préfixe de texte, autre dossier

        var moved = await svc.MovePathAsync(F("src"), F("lib"));

        Assert.Equal(2, moved.Count);
        Assert.Equal(F("lib/a.cs"), svc.GetInfo(a.Id).Path);
        Assert.Equal(F("lib/sub/b.cs"), svc.GetInfo(b.Id).Path);
        Assert.Equal(F("src2/c.cs"), svc.GetInfo(c.Id).Path);
    }

    [Fact]
    public async Task Rename_ThenOpeningTheNewPath_ReturnsTheSameDocument()
    {
        var (svc, _) = await Setup((F("a.cs"), "abc"));
        var doc = await svc.OpenDocumentAsync(F("a.cs"));
        await svc.MovePathAsync(F("a.cs"), F("b.cs"));

        var again = await svc.OpenDocumentAsync(F("b.cs"));

        Assert.Equal(doc.Id, again.Id);
        Assert.Single(svc.GetOpenDocuments());
    }

    [Fact]
    public async Task Rename_OfAFileWithoutOpenDocument_StillMovesIt()
    {
        var (svc, fs) = await Setup((F("a.txt"), "abc"));

        var moved = await svc.MovePathAsync(F("a.txt"), F("b.txt"));

        Assert.Empty(moved);
        Assert.True(fs.Files.ContainsKey(F("b.txt")));
    }

    [Fact]
    public async Task Rename_ToAnExistingName_Fails_AndKeepsTheOldPath()
    {
        var (svc, _) = await Setup((F("a.cs"), "a"), (F("b.cs"), "b"));
        var doc = await svc.OpenDocumentAsync(F("a.cs"));

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.MovePathAsync(F("a.cs"), F("b.cs")));

        Assert.Equal(ErrorCode.AlreadyExists, ex.Code);
        Assert.Equal(F("a.cs"), svc.GetInfo(doc.Id).Path);
    }

    [Fact]
    public async Task Rename_UntitledDocuments_AreNotTouched()
    {
        var (svc, _) = await Setup((F("a.txt"), "abc"));
        var untitled = svc.CreateDocument();

        await svc.MovePathAsync(F("a.txt"), F("b.txt"));

        Assert.Null(svc.GetInfo(untitled.Id).Path);
    }

    [Fact]
    public async Task Rename_WithAFileServiceThatCannotRename_ThrowsInvalidArgument()
    {
        var svc = new EditorService(new FakeFileService());

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.MovePathAsync(F("a.txt"), F("b.txt")));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Rename_SaveWritesToTheNewPath()
    {
        var (svc, fs) = await Setup((F("a.txt"), "abc"));
        var doc = await svc.OpenDocumentAsync(F("a.txt"));
        await svc.MovePathAsync(F("a.txt"), F("b.txt"));
        svc.ApplyEdit(doc.Id, Insert(0, 3, "!"));

        await svc.SaveAsync(doc.Id);

        Assert.Equal("abc!", fs.Files[F("b.txt")]);
        Assert.False(fs.Files.ContainsKey(F("a.txt")));
    }
}
