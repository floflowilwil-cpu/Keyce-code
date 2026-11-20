using App.Core.Models;
using App.Editor.Documents;

namespace App.Editor.Tests;

/// <summary>Horloge contrôlable pour tester le regroupement des frappes.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public class DocumentTests
{
    private static TextEdit Insert(int line, int col, string text) =>
        new(new TextRange(new TextPosition(line, col), new TextPosition(line, col)), text);

    private static TextEdit Remove(int l1, int c1, int l2, int c2) =>
        new(new TextRange(new TextPosition(l1, c1), new TextPosition(l2, c2)), "");

    private static Document Open(string text, TimeProvider? time = null) =>
        new(DocumentId.New(), "/tmp/test.cs", text, time);

    [Fact]
    public void NewDocument_IsClean_WithLanguageAndLineEnding()
    {
        var d = Open("a\r\nb\r\n");
        Assert.False(d.IsDirty);
        Assert.Equal("csharp", d.LanguageId);
        Assert.Equal(LineEnding.CrLf, d.LineEnding);
    }

    [Fact]
    public void Save_RestoresOriginalLineEnding()
    {
        var d = Open("a\r\nb");
        d.Apply(Insert(1, 1, "!"));
        Assert.Equal("a\r\nb!", d.GetTextForSave());
    }

    [Fact]
    public void Apply_MarksDirty_AndUndoRestoresCleanState()
    {
        var d = Open("abc");
        d.Apply(Insert(0, 3, "d"));
        Assert.True(d.IsDirty);
        d.Undo();
        Assert.False(d.IsDirty);
        Assert.Equal("abc", d.Text.GetText());
    }

    [Fact]
    public void UndoRedo_RoundTrip()
    {
        var d = Open("hello");
        d.Apply(Remove(0, 0, 0, 5));
        Assert.Equal("", d.Text.GetText());
        d.Undo();
        Assert.Equal("hello", d.Text.GetText());
        d.Redo();
        Assert.Equal("", d.Text.GetText());
    }

    [Fact]
    public void ConsecutiveTyping_IsOneUndoStep()
    {
        var time = new ManualTimeProvider();
        var d = Open("", time);
        d.Apply(Insert(0, 0, "a"));
        d.Apply(Insert(0, 1, "b"));
        d.Apply(Insert(0, 2, "c"));
        Assert.Equal("abc", d.Text.GetText());
        d.Undo();
        Assert.Equal("", d.Text.GetText());
        Assert.False(d.CanUndo);
    }

    [Fact]
    public void TypingAfterPause_StartsNewUndoStep()
    {
        var time = new ManualTimeProvider();
        var d = Open("", time);
        d.Apply(Insert(0, 0, "a"));
        time.Advance(TimeSpan.FromSeconds(5));
        d.Apply(Insert(0, 1, "b"));
        d.Undo();
        Assert.Equal("a", d.Text.GetText());
    }

    [Fact]
    public void Newline_StartsNewUndoStep()
    {
        var time = new ManualTimeProvider();
        var d = Open("", time);
        d.Apply(Insert(0, 0, "a"));
        d.Apply(Insert(0, 1, "\n"));
        d.Undo();
        Assert.Equal("a", d.Text.GetText());
    }

    [Fact]
    public void NewEdit_ClearsRedo()
    {
        var d = Open("x");
        d.Apply(Insert(0, 1, "y"));
        d.Undo();
        Assert.True(d.CanRedo);
        d.Apply(Insert(0, 1, "z"));
        Assert.False(d.CanRedo);
        Assert.Equal("xz", d.Text.GetText());
    }

    [Fact]
    public void MarkSaved_ThenEdit_IsDirty_AndUndoReturnsToClean()
    {
        var time = new ManualTimeProvider();
        var d = Open("", time);
        d.Apply(Insert(0, 0, "a"));
        d.MarkSaved();
        Assert.False(d.IsDirty);
        d.Apply(Insert(0, 1, "b")); // ne doit pas fusionner avec l'étape enregistrée
        Assert.True(d.IsDirty);
        d.Undo();
        Assert.Equal("a", d.Text.GetText());
        Assert.False(d.IsDirty);
    }

    [Fact]
    public void SavedStateLostInRedoBranch_StaysDirty()
    {
        var d = Open("");
        d.Apply(Insert(0, 0, "a"));
        d.Apply(Insert(0, 1, "\n"));
        d.MarkSaved();            // enregistré à 2 étapes
        d.Undo();                 // 1 étape, redo possible
        d.Apply(Insert(0, 1, "z")); // la branche enregistrée est perdue
        d.Undo();
        d.Undo();
        Assert.True(d.IsDirty);   // retour à "" mais le fichier contient "a\n"
    }

    [Fact]
    public void Apply_NoOp_ReturnsNull_AndDoesNotDirty()
    {
        var d = Open("abc");
        Assert.Null(d.Apply(Insert(0, 1, "")));
        Assert.False(d.IsDirty);
    }

    [Fact]
    public void Apply_NormalizesNewTextLineEndings()
    {
        var d = Open("");
        d.Apply(Insert(0, 0, "a\r\nb"));
        Assert.Equal("a\nb", d.Text.GetText());
        Assert.Equal(2, d.Text.LineCount);
    }

    [Fact]
    public void Undo_ReportsInverseEdits_ThatReproduceThePreviousText()
    {
        var d = Open("hello world");
        d.Apply(new TextEdit(new TextRange(new TextPosition(0, 6), new TextPosition(0, 11)), "there"));
        var edits = d.Undo();
        Assert.NotNull(edits);
        Assert.Single(edits!);
        Assert.Equal("world", edits![0].NewText);
        Assert.Equal(new TextPosition(0, 6), edits[0].Range.Start);
        Assert.Equal(new TextPosition(0, 11), edits[0].Range.End);
    }

    [Fact]
    public void Version_IncreasesOnEveryChange()
    {
        var d = Open("a");
        int v0 = d.Version;
        d.Apply(Insert(0, 1, "b"));
        d.Undo();
        Assert.True(d.Version >= v0 + 2);
    }
}
