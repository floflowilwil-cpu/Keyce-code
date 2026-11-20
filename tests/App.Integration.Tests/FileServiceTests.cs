using System.Collections.Concurrent;
using System.Text;
using App.Core.Errors;
using App.Core.Models;
using App.Integrations.Files;

namespace App.Integration.Tests;

public class FileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyce-fs-" + Guid.NewGuid().ToString("N"));
    private readonly FileService _service = new();

    public FileServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* nettoyage au mieux */ }
    }

    private string P(string name) => Path.Combine(_root, name);

    private static async Task<T> WaitFor<T>(Func<T?> probe, int timeoutMs = 5000) where T : class
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            var value = probe();
            if (value is not null) return value;
            await Task.Delay(25);
        }
        throw new TimeoutException("L'événement attendu n'est pas arrivé.");
    }

    // ---------- ReadAsync ----------

    [Fact]
    public async Task ReadAsync_ReturnsFileContent()
    {
        await File.WriteAllTextAsync(P("a.txt"), "bonjour\nélève");
        Assert.Equal("bonjour\nélève", await _service.ReadAsync(P("a.txt")));
    }

    [Fact]
    public async Task ReadAsync_StripsUtf8Bom()
    {
        await File.WriteAllBytesAsync(P("bom.txt"), [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']);
        Assert.Equal("hi", await _service.ReadAsync(P("bom.txt")));
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.ReadAsync(P("nope.txt")));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task ReadAsync_MissingDirectory_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.ReadAsync(P("no/dir/a.txt")));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task ReadAsync_EmptyPath_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.ReadAsync("  "));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task ReadAsync_CancelledToken_ThrowsOperationCanceled()
    {
        await File.WriteAllTextAsync(P("a.txt"), "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ReadAsync(P("a.txt"), cts.Token));
    }

    // ---------- WriteAsync ----------

    [Fact]
    public async Task WriteAsync_CreatesAndOverwrites()
    {
        await _service.WriteAsync(P("w.txt"), "premier");
        await _service.WriteAsync(P("w.txt"), "second");
        Assert.Equal("second", await File.ReadAllTextAsync(P("w.txt")));
    }

    [Fact]
    public async Task WriteAsync_WritesUtf8WithoutBom_AndKeepsLineEndings()
    {
        await _service.WriteAsync(P("u.txt"), "é\r\nb");
        byte[] bytes = await File.ReadAllBytesAsync(P("u.txt"));
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0x0D, 0x0A, (byte)'b' }, bytes);
    }

    [Fact]
    public async Task WriteAsync_MissingDirectory_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.WriteAsync(P("no/dir/a.txt"), "x"));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_File_CreatesEmptyFile()
    {
        await _service.CreateAsync(P("new.txt"), isDirectory: false);
        Assert.True(File.Exists(P("new.txt")));
        Assert.Equal(0L, new FileInfo(P("new.txt")).Length);
    }

    [Fact]
    public async Task CreateAsync_Directory_CreatesIncludingParents()
    {
        await _service.CreateAsync(P("a/b/c"), isDirectory: true);
        Assert.True(Directory.Exists(P("a/b/c")));
    }

    [Fact]
    public async Task CreateAsync_ExistingFile_ThrowsAlreadyExists_AndKeepsContent()
    {
        await File.WriteAllTextAsync(P("keep.txt"), "important");
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.CreateAsync(P("keep.txt"), false));
        Assert.Equal(ErrorCode.AlreadyExists, ex.Code);
        Assert.Equal("important", await File.ReadAllTextAsync(P("keep.txt")));
    }

    [Fact]
    public async Task CreateAsync_ExistingDirectory_ThrowsAlreadyExists()
    {
        Directory.CreateDirectory(P("d"));
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.CreateAsync(P("d"), true));
        Assert.Equal(ErrorCode.AlreadyExists, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_FileInMissingFolder_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.CreateAsync(P("no/dir/a.txt"), false));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_File_RemovesIt()
    {
        await File.WriteAllTextAsync(P("d.txt"), "x");
        await _service.DeleteAsync(P("d.txt"));
        Assert.False(File.Exists(P("d.txt")));
    }

    [Fact]
    public async Task DeleteAsync_Directory_RemovesItRecursively()
    {
        Directory.CreateDirectory(P("dir/sub"));
        await File.WriteAllTextAsync(P("dir/sub/f.txt"), "x");
        await _service.DeleteAsync(P("dir"));
        Assert.False(Directory.Exists(P("dir")));
    }

    [Fact]
    public async Task DeleteAsync_Missing_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.DeleteAsync(P("ghost")));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task DeleteAsync_FileSystemRoot_IsRefused()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.DeleteAsync(root));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    // ---------- WatchChanges ----------

    [Fact]
    public void WatchChanges_MissingPath_ThrowsNotFound()
    {
        var ex = Assert.Throws<AppException>(() => _service.WatchChanges(P("ghost"), _ => { }));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task WatchChanges_Directory_ReportsCreatedChangedDeleted()
    {
        var events = new ConcurrentQueue<FileChange>();
        using var watch = _service.WatchChanges(_root, events.Enqueue);

        string file = P("w.txt");
        await File.WriteAllTextAsync(file, "1");
        await WaitFor(() => events.FirstOrDefault(e => e.Kind == FileChangeKind.Created && e.Path == file));

        await Task.Delay(250);
        await File.WriteAllTextAsync(file, "22");
        await WaitFor(() => events.FirstOrDefault(e => e.Kind == FileChangeKind.Changed && e.Path == file));

        File.Delete(file);
        await WaitFor(() => events.FirstOrDefault(e => e.Kind == FileChangeKind.Deleted && e.Path == file));
    }

    [Fact]
    public async Task WatchChanges_Rename_ReportsOldPath()
    {
        string from = P("old.txt"), to = P("new.txt");
        await File.WriteAllTextAsync(from, "x");
        var events = new ConcurrentQueue<FileChange>();
        using var watch = _service.WatchChanges(_root, events.Enqueue);

        File.Move(from, to);
        var renamed = await WaitFor(() => events.FirstOrDefault(e => e.Kind == FileChangeKind.Renamed));
        Assert.Equal(to, renamed.Path);
        Assert.Equal(from, renamed.OldPath);
    }

    [Fact]
    public async Task WatchChanges_SingleFile_IgnoresSiblings()
    {
        string watched = P("watched.txt"), other = P("other.txt");
        await File.WriteAllTextAsync(watched, "1");
        var events = new ConcurrentQueue<FileChange>();
        using var watch = _service.WatchChanges(watched, events.Enqueue);

        await File.WriteAllTextAsync(other, "x");
        await File.WriteAllTextAsync(watched, "2");
        await WaitFor(() => events.FirstOrDefault(e => e.Path == watched));
        await Task.Delay(300);
        Assert.True(events.All(e => e.Path == watched));
    }

    [Fact]
    public async Task WatchChanges_AfterDispose_NoMoreEvents()
    {
        var events = new ConcurrentQueue<FileChange>();
        var watch = _service.WatchChanges(_root, events.Enqueue);
        watch.Dispose();

        await File.WriteAllTextAsync(P("late.txt"), "x");
        await Task.Delay(500);
        Assert.True(events.IsEmpty);
    }

    [Fact]
    public async Task WatchChanges_ThrowingCallback_DoesNotCrashTheWatcher()
    {
        int calls = 0;
        using var watch = _service.WatchChanges(_root, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        });

        await File.WriteAllTextAsync(P("a.txt"), "x");
        await WaitFor(() => Volatile.Read(ref calls) > 0 ? new object() : null);
        await File.WriteAllTextAsync(P("b.txt"), "x");
        await WaitFor(() => Volatile.Read(ref calls) > 1 ? new object() : null);
    }
}
