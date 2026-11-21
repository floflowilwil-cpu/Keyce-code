using App.Core.Errors;
using App.Core.Models;

namespace App.Editor.Tests;

/// <summary>Documents sans nom et « Enregistrer sous ».</summary>
public sealed class UntitledDocumentTests
{
    private static readonly string P = System.IO.Path.GetFullPath("/tmp/keyce-tests/open.cs");
    private static readonly string NewPy = System.IO.Path.GetFullPath("/tmp/keyce-tests/new.py");
    private static readonly string Other = System.IO.Path.GetFullPath("/tmp/keyce-tests/other.txt");

    private static (EditorService Svc, WatchableFileService Files) Create()
    {
        var files = new WatchableFileService();
        files.Files[P] = "class A {}";
        return (new EditorService(files), files);
    }

    private static TextEdit Insert(int line, int col, string text) =>
        new(new TextRange(new TextPosition(line, col), new TextPosition(line, col)), text);

    // ---------- Création ----------

    [Fact]
    public void CreateDocument_IsEmpty_Clean_AndHasNoPath()
    {
        var (svc, _) = Create();

        var info = svc.CreateDocument();

        Assert.Null(info.Path);
        Assert.False(info.IsDirty);
        Assert.Equal("plaintext", info.LanguageId);
        Assert.Equal("", svc.GetText(info.Id));
        Assert.Contains(svc.GetOpenDocuments(), d => d.Id == info.Id);
    }

    [Fact]
    public void CreateDocument_CanBeEdited_AndBecomesDirty()
    {
        var (svc, _) = Create();
        var info = svc.CreateDocument();

        svc.ApplyEdit(info.Id, Insert(0, 0, "bonjour"));

        Assert.Equal("bonjour", svc.GetText(info.Id));
        Assert.True(svc.GetInfo(info.Id).IsDirty);
    }

    [Fact]
    public void CreateDocument_TwiceGivesIndependentDocuments()
    {
        var (svc, _) = Create();
        var a = svc.CreateDocument();
        var b = svc.CreateDocument();

        svc.ApplyEdit(a.Id, Insert(0, 0, "A"));

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("A", svc.GetText(a.Id));
        Assert.Equal("", svc.GetText(b.Id));
    }

    [Fact]
    public async Task SaveAsync_OnUntitledDocument_RequiresSaveAs()
    {
        var (svc, files) = Create();
        var info = svc.CreateDocument();

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.SaveAsync(info.Id));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Equal(0, files.Writes);
    }

    [Fact]
    public void CloseDocument_WorksOnUntitledDocument()
    {
        var (svc, _) = Create();
        var info = svc.CreateDocument();

        Assert.True(svc.CloseDocument(info.Id));
        Assert.DoesNotContain(svc.GetOpenDocuments(), d => d.Id == info.Id);
    }

    // ---------- Enregistrer sous ----------

    [Fact]
    public async Task SaveAs_WritesFile_SetsPathAndLanguage_AndMarksClean()
    {
        var (svc, files) = Create();
        var info = svc.CreateDocument();
        svc.ApplyEdit(info.Id, Insert(0, 0, "x = 1"));
        var states = new List<DocumentInfo>();
        svc.DocumentStateChanged += (_, i) => states.Add(i);

        await svc.SaveAsAsync(info.Id, NewPy);

        Assert.Equal("x = 1", files.Files[NewPy]);
        var after = svc.GetInfo(info.Id);
        Assert.Equal(NewPy, after.Path);
        Assert.Equal("python", after.LanguageId);
        Assert.False(after.IsDirty);
        Assert.Equal(NewPy, Assert.Single(states).Path);
    }

    [Fact]
    public async Task SaveAs_ThenSaveAsync_WritesToTheNewPath()
    {
        var (svc, files) = Create();
        var info = svc.CreateDocument();
        svc.ApplyEdit(info.Id, Insert(0, 0, "a"));
        await svc.SaveAsAsync(info.Id, NewPy);

        svc.ApplyEdit(info.Id, Insert(0, 1, "b"));
        await svc.SaveAsync(info.Id);

        Assert.Equal("ab", files.Files[NewPy]);
    }

    [Fact]
    public async Task SaveAs_MakesTheDocumentFindableByItsPath()
    {
        var (svc, _) = Create();
        var info = svc.CreateDocument();
        await svc.SaveAsAsync(info.Id, NewPy);

        var reopened = await svc.OpenDocumentAsync(NewPy);

        Assert.Equal(info.Id, reopened.Id);
    }

    [Fact]
    public async Task SaveAs_StartsWatching_AndDetectsLaterExternalChanges()
    {
        var (svc, files) = Create();
        var events = new List<ExternalChangeEventArgs>();
        svc.ExternalChange += (_, e) => events.Add(e);
        var info = svc.CreateDocument();
        svc.ApplyEdit(info.Id, Insert(0, 0, "v1"));
        await svc.SaveAsAsync(info.Id, NewPy);
        Assert.Equal(1, files.WatcherCount);

        files.Files[NewPy] = "v2";
        await svc.CheckExternalChangeAsync(info.Id);

        Assert.Equal("v2", svc.GetText(info.Id));
        Assert.Equal(ExternalChangeKind.Reloaded, Assert.Single(events).Kind);
    }

    [Fact]
    public async Task SaveAs_OwnWrite_IsNotReportedAsExternalChange()
    {
        var (svc, _) = Create();
        var events = new List<ExternalChangeEventArgs>();
        svc.ExternalChange += (_, e) => events.Add(e);
        var info = svc.CreateDocument();
        svc.ApplyEdit(info.Id, Insert(0, 0, "v1"));

        await svc.SaveAsAsync(info.Id, NewPy);
        await svc.CheckExternalChangeAsync(info.Id);

        Assert.Empty(events);
    }

    [Fact]
    public async Task SaveAs_ToTheFileOfAnotherOpenDocument_IsRefused()
    {
        var (svc, files) = Create();
        await svc.OpenDocumentAsync(P);
        var info = svc.CreateDocument();
        svc.ApplyEdit(info.Id, Insert(0, 0, "mine"));

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.SaveAsAsync(info.Id, P));

        Assert.Equal(ErrorCode.Conflict, ex.Code);
        Assert.Equal("class A {}", files.Files[P]);
        Assert.Null(svc.GetInfo(info.Id).Path);
        Assert.True(svc.GetInfo(info.Id).IsDirty);
    }

    [Fact]
    public async Task SaveAs_OnExistingDocument_MovesItToTheNewPath()
    {
        var (svc, files) = Create();
        var original = await svc.OpenDocumentAsync(P);
        svc.ApplyEdit(original.Id, Insert(0, 0, "// "));

        await svc.SaveAsAsync(original.Id, Other);

        Assert.Equal("// class A {}", files.Files[Other]);
        Assert.Equal("class A {}", files.Files[P]); // l'ancien fichier n'est pas touché
        Assert.Equal(Other, svc.GetInfo(original.Id).Path);
        Assert.Equal("plaintext", svc.GetInfo(original.Id).LanguageId);
        Assert.Equal(1, files.DisposedWatchers);    // l'ancienne surveillance est arrêtée
        Assert.Equal(2, files.WatcherCount);        // une nouvelle démarre

        // L'ancien chemin est libre : l'ouvrir donne un nouveau document.
        var reopened = await svc.OpenDocumentAsync(P);
        Assert.NotEqual(original.Id, reopened.Id);
        Assert.Equal(original.Id, (await svc.OpenDocumentAsync(Other)).Id);
    }

    [Fact]
    public async Task SaveAs_ToTheSamePath_BehavesLikeSave()
    {
        var (svc, files) = Create();
        var info = await svc.OpenDocumentAsync(P);
        svc.ApplyEdit(info.Id, Insert(0, 0, "// "));

        await svc.SaveAsAsync(info.Id, P);

        Assert.Equal("// class A {}", files.Files[P]);
        Assert.False(svc.GetInfo(info.Id).IsDirty);
        Assert.Equal(1, files.Writes);
        Assert.Equal(1, files.WatcherCount); // pas de nouvelle surveillance
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveAs_WithEmptyPath_IsRefused(string path)
    {
        var (svc, _) = Create();
        var info = svc.CreateDocument();

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.SaveAsAsync(info.Id, path));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task SaveAs_OnUnknownDocument_Throws()
    {
        var (svc, _) = Create();
        var ex = await Assert.ThrowsAsync<AppException>(() => svc.SaveAsAsync(DocumentId.New(), NewPy));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }
}
