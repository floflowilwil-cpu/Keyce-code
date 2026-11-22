using App.Core.Errors;
using App.Integrations.Files;

namespace App.Integration.Tests;

/// <summary>Renommer / déplacer un fichier ou un dossier (IFileService.MoveAsync).</summary>
public sealed class FileMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyce-mv-" + Guid.NewGuid().ToString("N"));
    private readonly FileService _service = new();

    public FileMoveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* nettoyage au mieux */ }
    }

    private string P(string name) => Path.Combine(_root, name);

    [Fact]
    public async Task MoveAsync_RenamesAFile()
    {
        await File.WriteAllTextAsync(P("a.txt"), "hello");

        await _service.MoveAsync(P("a.txt"), P("b.txt"));

        Assert.False(File.Exists(P("a.txt")));
        Assert.Equal("hello", await File.ReadAllTextAsync(P("b.txt")));
    }

    [Fact]
    public async Task MoveAsync_RenamesAFolderWithItsContent()
    {
        Directory.CreateDirectory(P("d1"));
        await File.WriteAllTextAsync(P("d1/x.cs"), "x");

        await _service.MoveAsync(P("d1"), P("d2"));

        Assert.False(Directory.Exists(P("d1")));
        Assert.True(File.Exists(P("d2/x.cs")));
    }

    [Fact]
    public async Task MoveAsync_ExistingDestination_ThrowsAlreadyExists_AndChangesNothing()
    {
        await File.WriteAllTextAsync(P("a.txt"), "a");
        await File.WriteAllTextAsync(P("b.txt"), "b");

        var ex = await Assert.ThrowsAsync<AppException>(() => _service.MoveAsync(P("a.txt"), P("b.txt")));

        Assert.Equal(ErrorCode.AlreadyExists, ex.Code);
        Assert.Equal("a", await File.ReadAllTextAsync(P("a.txt")));
        Assert.Equal("b", await File.ReadAllTextAsync(P("b.txt")));
    }

    [Fact]
    public async Task MoveAsync_MissingSource_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.MoveAsync(P("absent"), P("x")));

        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task MoveAsync_MissingDestinationFolder_ThrowsNotFound()
    {
        await File.WriteAllTextAsync(P("a.txt"), "a");

        var ex = await Assert.ThrowsAsync<AppException>(() => _service.MoveAsync(P("a.txt"), P("nope/a.txt")));

        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task MoveAsync_FolderIntoItself_ThrowsInvalidArgument()
    {
        Directory.CreateDirectory(P("d"));

        var ex = await Assert.ThrowsAsync<AppException>(() => _service.MoveAsync(P("d"), P("d/sub")));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task MoveAsync_SameName_DoesNothing()
    {
        await File.WriteAllTextAsync(P("a.txt"), "a");

        await _service.MoveAsync(P("a.txt"), P("a.txt"));

        Assert.True(File.Exists(P("a.txt")));
    }

    [Fact]
    public async Task MoveAsync_CaseOnlyChange_Works()
    {
        await File.WriteAllTextAsync(P("a.txt"), "a");

        await _service.MoveAsync(P("a.txt"), P("A.TXT"));

        var names = Directory.GetFiles(_root).Select(Path.GetFileName).ToList();
        Assert.Contains("A.TXT", names);
        Assert.DoesNotContain("a.txt", names);
    }

    [Fact]
    public async Task MoveAsync_CaseOnlyChange_OfAFolder_Works()
    {
        Directory.CreateDirectory(P("src"));

        await _service.MoveAsync(P("src"), P("Src"));

        Assert.Contains("Src", Directory.GetDirectories(_root).Select(Path.GetFileName).ToList());
    }
}
