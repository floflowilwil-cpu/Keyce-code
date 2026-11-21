using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Editor.Tests;

/// <summary>Faux service de fichiers dont on peut simuler les changements disque.</summary>
internal sealed class WatchableFileService : IFileService
{
    private Action<FileChange>? _onChange;

    public Dictionary<string, string> Files { get; } = new();
    public int WatcherCount { get; private set; }
    public int DisposedWatchers { get; private set; }
    public int Writes { get; private set; }

    public Task<string> ReadAsync(string path, CancellationToken ct = default) =>
        Files.TryGetValue(path, out var c)
            ? Task.FromResult(c)
            : throw new AppException(ErrorCode.NotFound, path);

    public Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        Writes++;
        Files[path] = content;
        return Task.CompletedTask;
    }

    public Task CreateAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public IDisposable WatchChanges(string path, Action<FileChange> onChange)
    {
        WatcherCount++;
        _onChange = onChange;
        return new Handle(this);
    }

    /// <summary>Simule l'événement du watcher (sans modifier le contenu).</summary>
    public void Raise(FileChange change) => _onChange?.Invoke(change);

    private sealed class Handle(WatchableFileService owner) : IDisposable
    {
        public void Dispose() => owner.DisposedWatchers++;
    }
}

public class ExternalChangeTests
{
    private static readonly string P = System.IO.Path.GetFullPath("/tmp/keyce-tests/ext.cs");

    private static async Task<(EditorService svc, WatchableFileService files, DocumentId id, List<ExternalChangeEventArgs> events)>
        Open(string content = "abc\ndef")
    {
        var files = new WatchableFileService();
        files.Files[P] = content;
        var svc = new EditorService(files);
        var events = new List<ExternalChangeEventArgs>();
        svc.ExternalChange += (_, e) => events.Add(e);
        var info = await svc.OpenDocumentAsync(P);
        return (svc, files, info.Id, events);
    }

    private static TextEdit Insert(int line, int col, string text) =>
        new(new TextRange(new TextPosition(line, col), new TextPosition(line, col)), text);

    [Fact]
    public async Task Open_StartsWatching_AndCloseStopsIt()
    {
        var (svc, files, id, _) = await Open();
        Assert.Equal(1, files.WatcherCount);

        svc.CloseDocument(id);
        Assert.Equal(1, files.DisposedWatchers);
    }

    [Fact]
    public async Task Dispose_StopsAllWatchers()
    {
        var (svc, files, _, _) = await Open();
        svc.Dispose();
        Assert.Equal(1, files.DisposedWatchers);
    }

    [Fact]
    public async Task CleanDocument_IsReloaded_WhenFileChangesOnDisk()
    {
        var (svc, files, id, events) = await Open("abc\ndef");
        var changes = new List<DocumentChangedEventArgs>();
        svc.DocumentChanged += (_, e) => changes.Add(e);

        files.Files[P] = "nouveau contenu";
        await svc.CheckExternalChangeAsync(id);

        Assert.Equal("nouveau contenu", svc.GetText(id));
        Assert.False(svc.GetInfo(id).IsDirty);
        Assert.Single(events);
        Assert.Equal(ExternalChangeKind.Reloaded, events[0].Kind);

        // Les consommateurs incrémentaux (LSP, vue) reçoivent une modification globale.
        var edit = Assert.Single(Assert.Single(changes).Edits);
        Assert.Equal("nouveau contenu", edit.NewText);
        Assert.Equal(new TextPosition(0, 0), edit.Range.Start);
        Assert.Equal(new TextPosition(1, 3), edit.Range.End);
    }

    [Fact]
    public async Task Reload_ClearsUndoHistory()
    {
        var (svc, files, id, _) = await Open("abc");

        files.Files[P] = "xyz";
        await svc.CheckExternalChangeAsync(id);
        svc.Undo(id);

        Assert.Equal("xyz", svc.GetText(id));
    }

    [Fact]
    public async Task Reload_KeepsCaretsInsideTheNewText()
    {
        var (svc, files, id, _) = await Open("abc\ndef\nghi");
        svc.SetCaretSet(id, CaretSet.Single(new TextPosition(2, 3)));

        files.Files[P] = "x";
        await svc.CheckExternalChangeAsync(id);

        var caret = svc.GetCaretSet(id).Carets[0];
        Assert.Equal(new TextPosition(0, 1), caret.Position);
    }

    [Fact]
    public async Task DirtyDocument_ReportsConflict_WithoutOverwriting()
    {
        var (svc, files, id, events) = await Open("abc");
        svc.ApplyEdit(id, Insert(0, 3, "!"));

        files.Files[P] = "version disque";
        await svc.CheckExternalChangeAsync(id);

        Assert.Equal("abc!", svc.GetText(id));
        Assert.True(svc.GetInfo(id).IsDirty);
        Assert.Equal(ExternalChangeKind.Conflict, Assert.Single(events).Kind);
    }

    [Fact]
    public async Task Conflict_IsReportedOnlyOnce_PerDiskVersion()
    {
        var (svc, files, id, events) = await Open("abc");
        svc.ApplyEdit(id, Insert(0, 3, "!"));

        files.Files[P] = "version disque";
        await svc.CheckExternalChangeAsync(id);
        await svc.CheckExternalChangeAsync(id);
        Assert.Single(events);

        files.Files[P] = "autre version disque";
        await svc.CheckExternalChangeAsync(id);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task ReloadFromDisk_ResolvesConflict_DiscardingLocalChanges()
    {
        var (svc, files, id, _) = await Open("abc");
        svc.ApplyEdit(id, Insert(0, 3, "!"));
        files.Files[P] = "version disque";
        await svc.CheckExternalChangeAsync(id);

        await svc.ReloadFromDiskAsync(id);

        Assert.Equal("version disque", svc.GetText(id));
        Assert.False(svc.GetInfo(id).IsDirty);
    }

    [Fact]
    public async Task KeepLocalVersion_ResolvesConflict_AndKeepsDocumentDirty()
    {
        var (svc, files, id, events) = await Open("abc");
        svc.ApplyEdit(id, Insert(0, 3, "!"));
        files.Files[P] = "version disque";
        await svc.CheckExternalChangeAsync(id);

        await svc.KeepLocalVersionAsync(id);
        await svc.CheckExternalChangeAsync(id);

        Assert.Equal("abc!", svc.GetText(id));
        Assert.True(svc.GetInfo(id).IsDirty);
        Assert.Single(events); // plus de nouveau conflit pour la même version disque

        await svc.SaveAsync(id);
        Assert.Equal("abc!", files.Files[P]);
    }

    [Fact]
    public async Task OwnSave_IsNotReportedAsExternalChange()
    {
        var (svc, files, id, events) = await Open("abc");
        svc.ApplyEdit(id, Insert(0, 3, "!"));

        await svc.SaveAsync(id);
        await svc.CheckExternalChangeAsync(id); // l'écho du watcher

        Assert.Empty(events);
        Assert.Equal("abc!", svc.GetText(id));
        Assert.False(svc.GetInfo(id).IsDirty);
    }

    [Fact]
    public async Task OwnSave_WithCrLf_IsNotReportedAsExternalChange()
    {
        var (svc, _, id, events) = await Open("a\r\nb");
        svc.ApplyEdit(id, Insert(0, 1, "x"));

        await svc.SaveAsync(id);
        await svc.CheckExternalChangeAsync(id);

        Assert.Empty(events);
    }

    [Fact]
    public async Task UnchangedFile_DoesNothing()
    {
        var (svc, _, id, events) = await Open("abc");
        await svc.CheckExternalChangeAsync(id);
        Assert.Empty(events);
    }

    [Fact]
    public async Task DeletedFile_IsReported_AndContentIsKept()
    {
        var (svc, files, id, events) = await Open("abc");
        files.Files.Remove(P);

        await svc.CheckExternalChangeAsync(id);

        Assert.Equal(ExternalChangeKind.Deleted, Assert.Single(events).Kind);
        Assert.Equal("abc", svc.GetText(id));
    }

    [Fact]
    public async Task WatcherEvent_TriggersReload()
    {
        var (svc, files, id, events) = await Open("abc");
        var reloaded = new TaskCompletionSource();
        svc.ExternalChange += (_, _) => reloaded.TrySetResult();

        files.Files[P] = "modifié";
        files.Raise(new FileChange(P, FileChangeKind.Changed));

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("modifié", svc.GetText(id));
        Assert.Equal(ExternalChangeKind.Reloaded, events[0].Kind);
    }

    [Fact]
    public async Task WatcherDeletedEvent_IsReported()
    {
        var (svc, files, _, events) = await Open("abc");
        var seen = new TaskCompletionSource();
        svc.ExternalChange += (_, _) => seen.TrySetResult();

        files.Raise(new FileChange(P, FileChangeKind.Deleted));

        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalChangeKind.Deleted, events[0].Kind);
    }

    [Fact]
    public async Task ClosedDocument_IgnoresLateEvents()
    {
        var (svc, files, id, events) = await Open("abc");
        svc.CloseDocument(id);

        await Assert.ThrowsAsync<AppException>(() => svc.CheckExternalChangeAsync(id));
        Assert.Empty(events);
        _ = files;
    }
}
