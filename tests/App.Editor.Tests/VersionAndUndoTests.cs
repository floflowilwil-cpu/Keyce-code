using System.Collections.Concurrent;
using App.Core.Models;
using App.Editor.Editing;

namespace App.Editor.Tests;

/// <summary>Numéro de version des documents (pour la synchronisation LSP) et regroupement des frappes en annulation.</summary>
public sealed class VersionAndUndoTests
{
    private static readonly string FilePath = Path.GetFullPath("/tmp/keyce-tests/version.cs");

    private static TextEdit Insert(int line, int column, string text) =>
        new(new TextRange(new TextPosition(line, column), new TextPosition(line, column)), text);

    private static async Task<(EditorService Svc, DocumentId Id, ManualTimeProvider Time)> Open(string content = "")
    {
        var files = new FakeFileService();
        files.Files[FilePath] = content;
        var time = new ManualTimeProvider();
        var svc = new EditorService(files, time);
        var info = await svc.OpenDocumentAsync(FilePath);
        return (svc, info.Id, time);
    }

    // ---------- Version ----------

    [Fact]
    public async Task OpenedDocument_HasAVersion_ThatGrowsWithEveryChange()
    {
        var (svc, id, _) = await Open("abc");
        int v0 = svc.GetInfo(id).Version;

        svc.ApplyEdit(id, Insert(0, 3, "d"));
        int v1 = svc.GetInfo(id).Version;
        svc.ApplyEdit(id, Insert(0, 4, "e"));
        int v2 = svc.GetInfo(id).Version;

        Assert.True(v1 > v0);
        Assert.True(v2 > v1);
    }

    [Fact]
    public async Task EventVersion_IsTheDocumentVersionAfterTheEdit()
    {
        var (svc, id, _) = await Open("abc");
        var events = new List<DocumentChangedEventArgs>();
        svc.DocumentChanged += (_, e) => events.Add(e);

        svc.ApplyEdit(id, Insert(0, 3, "d"));

        Assert.Single(events);
        Assert.Equal(svc.GetInfo(id).Version, events[0].Version);
    }

    [Fact]
    public async Task EventVersions_AreStrictlyIncreasing_AlsoForUndoRedoAndBatches()
    {
        var (svc, id, _) = await Open("abc");
        var versions = new List<int>();
        svc.DocumentChanged += (_, e) => versions.Add(e.Version);

        svc.ApplyEdit(id, Insert(0, 0, "x"));
        svc.ApplyEdits(id, [Insert(0, 0, "a"), Insert(0, 2, "b")]);   // deux modifications dans un lot
        svc.Undo(id);
        svc.Redo(id);
        svc.Undo(id);

        Assert.Equal(5, versions.Count);
        for (int i = 1; i < versions.Count; i++)
            Assert.True(versions[i] > versions[i - 1]);
    }

    [Fact]
    public async Task Version_NeverGoesBack_AfterUndo()
    {
        var (svc, id, _) = await Open("abc");
        svc.ApplyEdit(id, Insert(0, 3, "d"));
        int afterEdit = svc.GetInfo(id).Version;

        svc.Undo(id);

        Assert.True(svc.GetInfo(id).Version > afterEdit);
    }

    [Fact]
    public async Task ReloadFromDisk_AlsoIncreasesTheVersion()
    {
        var files = new FakeFileService();
        files.Files[FilePath] = "old";
        var svc = new EditorService(files);
        var id = (await svc.OpenDocumentAsync(FilePath)).Id;
        svc.ApplyEdit(id, Insert(0, 3, "!"));
        int before = svc.GetInfo(id).Version;
        var events = new List<DocumentChangedEventArgs>();
        svc.DocumentChanged += (_, e) => events.Add(e);

        files.Files[FilePath] = "disk";
        await svc.ReloadFromDiskAsync(id);

        Assert.Single(events);
        Assert.True(events[0].Version > before);
        Assert.Equal(events[0].Version, svc.GetInfo(id).Version);
    }

    [Fact]
    public async Task StateChangedEvent_CarriesTheCurrentVersion()
    {
        var (svc, id, _) = await Open("abc");
        var states = new ConcurrentQueue<DocumentInfo>();
        svc.DocumentStateChanged += (_, i) => states.Enqueue(i);

        svc.ApplyEdit(id, Insert(0, 3, "d"));   // le document devient « modifié »

        Assert.True(states.TryDequeue(out var state));
        Assert.Equal(svc.GetInfo(id).Version, state!.Version);
        Assert.True(state.IsDirty);
    }

    // ---------- Annuler : une frappe n'est pas un pas d'annulation à elle seule ----------

    [Fact]
    public async Task TypingAWordWithOneCaret_IsOneUndoStep()
    {
        var (svc, id, _) = await Open("");
        var commands = new EditorCommands(svc);

        foreach (char c in "hello") commands.TypeText(id, c.ToString());
        Assert.Equal("hello", svc.GetText(id));

        svc.Undo(id);
        Assert.Equal("", svc.GetText(id));      // tout le mot, pas seulement la dernière lettre

        svc.Redo(id);
        Assert.Equal("hello", svc.GetText(id)); // Ctrl+Y rétablit le mot entier
    }

    [Fact]
    public async Task TypingAfterAPause_StartsANewUndoStep()
    {
        var (svc, id, time) = await Open("");
        var commands = new EditorCommands(svc);

        commands.TypeText(id, "ab");
        time.Advance(TimeSpan.FromSeconds(5));
        commands.TypeText(id, "cd");

        svc.Undo(id);
        Assert.Equal("ab", svc.GetText(id));
    }

    [Fact]
    public async Task NewLine_SplitsUndoSteps()
    {
        var (svc, id, _) = await Open("");
        var commands = new EditorCommands(svc);

        commands.TypeText(id, "ab");
        commands.InsertNewLine(id);
        commands.TypeText(id, "cd");

        svc.Undo(id);
        Assert.Equal("ab\n", svc.GetText(id));
        svc.Undo(id);
        Assert.Equal("ab", svc.GetText(id));
    }

    [Fact]
    public async Task TypingWithSeveralCarets_StillWorks_AndUndoesInOneStep()
    {
        var (svc, id, _) = await Open("ab");
        svc.SetCaretSet(id, new CaretSet([Caret.At(new TextPosition(0, 0)), Caret.At(new TextPosition(0, 2))]));
        var commands = new EditorCommands(svc);

        commands.TypeText(id, "X");
        Assert.Equal("XabX", svc.GetText(id));

        svc.Undo(id);
        Assert.Equal("ab", svc.GetText(id));
    }
}
