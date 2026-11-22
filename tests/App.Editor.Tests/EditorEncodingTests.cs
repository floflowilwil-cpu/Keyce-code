using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Editor.Tests;

/// <summary>Faux service de fichiers qui détecte et mémorise l'encodage.</summary>
internal sealed class EncodedFakeFileService : IEncodedFileService
{
    public Dictionary<string, FileContent> Files { get; } = new();
    public List<(string Path, string Text, FileEncoding Encoding)> Writes { get; } = new();
    public int PlainWrites { get; private set; }

    public Task<string> ReadAsync(string path, CancellationToken ct = default) =>
        Files.TryGetValue(path, out var c) ? Task.FromResult(c.Text) : throw new AppException(ErrorCode.NotFound, path);

    public Task<FileContent> ReadWithEncodingAsync(string path, CancellationToken ct = default) =>
        Files.TryGetValue(path, out var c) ? Task.FromResult(c) : throw new AppException(ErrorCode.NotFound, path);

    /// <summary>Texte que « relire avec cet encodage » doit produire, par (chemin, nom d'encodage).</summary>
    public Dictionary<(string Path, string Encoding), string> AsEncoding { get; } = new();

    public Task<FileContent> ReadAsEncodingAsync(string path, FileEncoding encoding, CancellationToken ct = default) =>
        AsEncoding.TryGetValue((path, encoding.Name), out var text)
            ? Task.FromResult(new FileContent(text, encoding))
            : throw new AppException(ErrorCode.NotFound, path);

    public Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        PlainWrites++;
        return Task.CompletedTask;
    }

    public Task WriteAsync(string path, string content, FileEncoding encoding, CancellationToken ct = default)
    {
        Writes.Add((path, content, encoding));
        Files[path] = new FileContent(content, encoding);
        return Task.CompletedTask;
    }

    public Task CreateAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public IDisposable WatchChanges(string path, Action<FileChange> onChange) => new Noop();

    private sealed class Noop : IDisposable { public void Dispose() { } }
}

public sealed class EditorEncodingTests
{
    private static readonly string P = System.IO.Path.GetFullPath("/tmp/keyce-tests/enc.txt");
    private static readonly string Q = System.IO.Path.GetFullPath("/tmp/keyce-tests/enc2.txt");

    private static TextEdit Insert(int line, int col, string text) =>
        new(new TextRange(new TextPosition(line, col), new TextPosition(line, col)), text);

    private static async Task<(EditorService Svc, EncodedFakeFileService Files, DocumentId Id)> Open(
        string text, FileEncoding encoding)
    {
        var files = new EncodedFakeFileService();
        files.Files[P] = new FileContent(text, encoding);
        var svc = new EditorService(files);
        var info = await svc.OpenDocumentAsync(P);
        return (svc, files, info.Id);
    }

    [Fact]
    public async Task Open_KeepsTheDetectedEncoding()
    {
        var (svc, _, id) = await Open("élève", FileEncoding.Windows1252);

        Assert.Equal(FileEncoding.Windows1252, svc.GetEncoding(id));
        Assert.Equal("élève", svc.GetText(id));
    }

    [Fact]
    public async Task Save_WritesWithTheSameEncoding()
    {
        var (svc, files, id) = await Open("é", FileEncoding.Windows1252);
        svc.ApplyEdit(id, Insert(0, 1, "!"));

        await svc.SaveAsync(id);

        var write = Assert.Single(files.Writes);
        Assert.Equal("é!", write.Text);
        Assert.Equal(FileEncoding.Windows1252, write.Encoding);
        Assert.Equal(0, files.PlainWrites);
    }

    [Fact]
    public async Task Save_KeepsTheUtf8Bom()
    {
        var (svc, files, id) = await Open("abc", FileEncoding.Utf8Bom);
        svc.ApplyEdit(id, Insert(0, 3, "d"));

        await svc.SaveAsync(id);

        Assert.Equal(FileEncoding.Utf8Bom, Assert.Single(files.Writes).Encoding);
    }

    [Fact]
    public async Task SetEncoding_ChangesTheEncodingUsedBySave_AndRaisesStateChanged()
    {
        var (svc, files, id) = await Open("abc", FileEncoding.Windows1252);
        var states = new List<DocumentInfo>();
        svc.DocumentStateChanged += (_, i) => states.Add(i);

        svc.SetEncoding(id, FileEncoding.Utf8);
        await svc.SaveAsync(id);

        Assert.Equal(FileEncoding.Utf8, svc.GetEncoding(id));
        Assert.Single(states);
        Assert.Equal(FileEncoding.Utf8, Assert.Single(files.Writes).Encoding);
    }

    [Fact]
    public async Task SetEncoding_ToTheSameValue_DoesNothing()
    {
        var (svc, _, id) = await Open("abc", FileEncoding.Utf8);
        var states = new List<DocumentInfo>();
        svc.DocumentStateChanged += (_, i) => states.Add(i);

        svc.SetEncoding(id, FileEncoding.Utf8);

        Assert.Empty(states);
    }

    [Fact]
    public async Task SetEncoding_DoesNotMarkTheDocumentDirty_NorChangeItsText()
    {
        var (svc, _, id) = await Open("abc", FileEncoding.Windows1252);

        svc.SetEncoding(id, FileEncoding.Utf16Le);

        Assert.False(svc.GetInfo(id).IsDirty);
        Assert.Equal("abc", svc.GetText(id));
    }

    [Fact]
    public async Task SetEncoding_WithUnknownEncoding_IsRefused()
    {
        var (svc, _, id) = await Open("abc", FileEncoding.Utf8);

        var ex = Assert.Throws<AppException>(() => svc.SetEncoding(id, new FileEncoding("klingon", false)));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task ExternalReload_UpdatesTheEncoding()
    {
        var (svc, files, id) = await Open("abc", FileEncoding.Utf8);

        files.Files[P] = new FileContent("abcé", FileEncoding.Windows1252);
        await svc.CheckExternalChangeAsync(id);

        Assert.Equal("abcé", svc.GetText(id));
        Assert.Equal(FileEncoding.Windows1252, svc.GetEncoding(id));
    }

    [Fact]
    public async Task ReloadFromDisk_UpdatesTheEncoding()
    {
        var (svc, files, id) = await Open("abc", FileEncoding.Utf8);
        svc.ApplyEdit(id, Insert(0, 3, "!"));

        files.Files[P] = new FileContent("disque", FileEncoding.Utf16Le);
        await svc.ReloadFromDiskAsync(id);

        Assert.Equal(FileEncoding.Utf16Le, svc.GetEncoding(id));
    }

    [Fact]
    public async Task NewDocument_IsUtf8_AndSaveAsUsesIt()
    {
        var files = new EncodedFakeFileService();
        var svc = new EditorService(files);
        var info = svc.CreateDocument();
        svc.ApplyEdit(info.Id, Insert(0, 0, "é"));

        Assert.Equal(FileEncoding.Utf8, svc.GetEncoding(info.Id));
        await svc.SaveAsAsync(info.Id, Q);

        Assert.Equal(FileEncoding.Utf8, Assert.Single(files.Writes).Encoding);
    }

    [Fact]
    public async Task SaveAs_KeepsTheDocumentEncoding()
    {
        var (svc, files, id) = await Open("é", FileEncoding.Windows1252);

        await svc.SaveAsAsync(id, Q);

        var write = Assert.Single(files.Writes);
        Assert.Equal(Q, write.Path);
        Assert.Equal(FileEncoding.Windows1252, write.Encoding);
    }

    [Fact]
    public async Task PlainFileService_FallsBackToUtf8()
    {
        var files = new FakeFileService();
        files.Files[P] = "abc";
        var svc = new EditorService(files);
        var info = await svc.OpenDocumentAsync(P);

        Assert.Equal(FileEncoding.Utf8, svc.GetEncoding(info.Id));
        svc.ApplyEdit(info.Id, Insert(0, 3, "d"));
        await svc.SaveAsync(info.Id);
        Assert.Equal("abcd", files.Files[P]);
    }

    // ---------- Ouverture / fermeture (pour le colorateur et le LSP) ----------

    [Fact]
    public async Task DocumentOpened_IsRaised_OnlyForNewlyOpenedDocuments()
    {
        var files = new EncodedFakeFileService();
        files.Files[P] = new FileContent("abc", FileEncoding.Utf8);
        var svc = new EditorService(files);
        var opened = new List<DocumentInfo>();
        svc.DocumentOpened += (_, i) => opened.Add(i);

        var first = await svc.OpenDocumentAsync(P);
        await svc.OpenDocumentAsync(P); // déjà ouvert : pas de nouvel événement

        Assert.Equal(first.Id, Assert.Single(opened).Id);
    }

    [Fact]
    public void DocumentOpened_IsRaised_ForCreatedDocuments()
    {
        var svc = new EditorService(new EncodedFakeFileService());
        var opened = new List<DocumentInfo>();
        svc.DocumentOpened += (_, i) => opened.Add(i);

        var info = svc.CreateDocument();

        Assert.Equal(info.Id, Assert.Single(opened).Id);
    }

    [Fact]
    public async Task DocumentClosed_IsRaised_WithThePathBeforeClosing_OnlyOnce()
    {
        var (svc, _, id) = await Open("abc", FileEncoding.Utf8);
        var closed = new List<DocumentInfo>();
        svc.DocumentClosed += (_, i) => closed.Add(i);

        Assert.True(svc.CloseDocument(id));
        Assert.False(svc.CloseDocument(id));

        var info = Assert.Single(closed);
        Assert.Equal(id, info.Id);
        Assert.Equal(P, info.Path);
    }
}
