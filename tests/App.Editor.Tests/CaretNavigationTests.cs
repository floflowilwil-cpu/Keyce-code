using App.Core.Contracts;
using App.Core.Models;
using App.Editor.Editing;

namespace App.Editor.Tests;

/// <summary>Déplacements de curseurs et sélections à la souris (contrat IEditorCommands).</summary>
public sealed class CaretNavigationTests
{
    private const string FilePath = "/tmp/keyce-tests/navigation.cs";

    private static TextPosition At(int line, int column) => new(line, column);

    private static async Task<(EditorService Editor, DocumentId Id, IEditorCommands Commands)> Create(
        string text, params Caret[] carets)
    {
        var files = new FakeFileService();
        files.Files[Path.GetFullPath(FilePath)] = text;
        var editor = new EditorService(files);
        var info = await editor.OpenDocumentAsync(FilePath);
        editor.SetCaretSet(info.Id, new CaretSet(carets.Length == 0 ? [Caret.At(At(0, 0))] : carets));
        return (editor, info.Id, new EditorCommands(editor));
    }

    private static Caret Select(int l1, int c1, int l2, int c2) =>
        Caret.FromSelection(new Selection(At(l1, c1), At(l2, c2)));

    private static TextPosition Primary(EditorService editor, DocumentId id) =>
        editor.GetCaretSet(id).Primary.Position;

    // ---------- Gauche / droite ----------

    [Fact]
    public async Task Right_MovesOneColumn_ThenWrapsToTheNextLine()
    {
        var (editor, id, commands) = await Create("ab\ncd", Caret.At(At(0, 1)));

        commands.MoveCarets(id, CaretMove.Right);
        Assert.Equal(At(0, 2), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.Right);
        Assert.Equal(At(1, 0), Primary(editor, id));
    }

    [Fact]
    public async Task Right_AtTheEndOfTheDocument_StaysThere()
    {
        var (editor, id, commands) = await Create("ab", Caret.At(At(0, 2)));
        commands.MoveCarets(id, CaretMove.Right);
        Assert.Equal(At(0, 2), Primary(editor, id));
    }

    [Fact]
    public async Task Left_MovesOneColumn_ThenWrapsToThePreviousLineEnd()
    {
        var (editor, id, commands) = await Create("abc\nd", Caret.At(At(1, 1)));

        commands.MoveCarets(id, CaretMove.Left);
        Assert.Equal(At(1, 0), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.Left);
        Assert.Equal(At(0, 3), Primary(editor, id));
    }

    [Fact]
    public async Task Left_AtTheStartOfTheDocument_StaysThere()
    {
        var (editor, id, commands) = await Create("ab", Caret.At(At(0, 0)));
        commands.MoveCarets(id, CaretMove.Left);
        Assert.Equal(At(0, 0), Primary(editor, id));
    }

    [Fact]
    public async Task Right_SkipsAWholeSurrogatePair()
    {
        var (editor, id, commands) = await Create("a😀b", Caret.At(At(0, 1)));

        commands.MoveCarets(id, CaretMove.Right);
        Assert.Equal(At(0, 3), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.Left);
        Assert.Equal(At(0, 1), Primary(editor, id));
    }

    [Fact]
    public async Task Right_WithSelection_CollapsesToItsEnd_AndLeftToItsStart()
    {
        var (editor, id, commands) = await Create("abcdef", Select(0, 1, 0, 4));
        commands.MoveCarets(id, CaretMove.Right);
        Assert.Equal(Caret.At(At(0, 4)), editor.GetCaretSet(id).Primary);

        commands.SetCaret(id, At(0, 0));
        editor.SetCaretSet(id, new CaretSet([Select(0, 4, 0, 1)])); // sélection inversée
        commands.MoveCarets(id, CaretMove.Left);
        Assert.Equal(Caret.At(At(0, 1)), editor.GetCaretSet(id).Primary);
    }

    [Fact]
    public async Task Right_WithShift_ExtendsTheSelection_KeepingTheAnchor()
    {
        var (editor, id, commands) = await Create("abcdef", Caret.At(At(0, 2)));

        commands.MoveCarets(id, CaretMove.Right, extendSelection: true);
        commands.MoveCarets(id, CaretMove.Right, extendSelection: true);

        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(0, 2), caret.Anchor);
        Assert.Equal(At(0, 4), caret.Position);
    }

    // ---------- Haut / bas ----------

    [Fact]
    public async Task UpDown_KeepTheDesiredColumn_AcrossShorterLines()
    {
        var (editor, id, commands) = await Create("abcdef\nab\nabcdef", Caret.At(At(0, 5)));

        commands.MoveCarets(id, CaretMove.Down);
        Assert.Equal(At(1, 2), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.Down);
        Assert.Equal(At(2, 5), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.Up);
        commands.MoveCarets(id, CaretMove.Up);
        Assert.Equal(At(0, 5), Primary(editor, id));
    }

    [Fact]
    public async Task HorizontalMove_ForgetsTheDesiredColumn()
    {
        var (editor, id, commands) = await Create("abcdef\nab\nabcdef", Caret.At(At(0, 5)));

        commands.MoveCarets(id, CaretMove.Down);          // (1,2), colonne voulue 5
        commands.MoveCarets(id, CaretMove.Left);          // (1,1)
        commands.MoveCarets(id, CaretMove.Down);          // colonne 1 maintenant

        Assert.Equal(At(2, 1), Primary(editor, id));
    }

    [Fact]
    public async Task Up_OnTheFirstLine_GoesToTheStart_AndDown_OnTheLastLine_GoesToTheEnd()
    {
        var (editor, id, commands) = await Create("abc\ndef", Caret.At(At(0, 2)));

        commands.MoveCarets(id, CaretMove.Up);
        Assert.Equal(At(0, 0), Primary(editor, id));

        commands.SetCaret(id, At(1, 1));
        commands.MoveCarets(id, CaretMove.Down);
        Assert.Equal(At(1, 3), Primary(editor, id));
    }

    [Fact]
    public async Task PageDown_AndPageUp_MoveByPageSize_AndClampAtTheEnds()
    {
        var (editor, id, commands) = await Create("a\nb\nc\nd\ne", Caret.At(At(0, 0)));

        commands.MoveCarets(id, CaretMove.PageDown, pageSize: 2);
        Assert.Equal(At(2, 0), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.PageDown, pageSize: 10);
        Assert.Equal(At(4, 0), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.PageUp, pageSize: 10);
        Assert.Equal(At(0, 0), Primary(editor, id));
    }

    // ---------- Début / fin ----------

    [Fact]
    public async Task Home_AlternatesBetweenFirstNonBlankAndColumnZero()
    {
        var (editor, id, commands) = await Create("    foo", Caret.At(At(0, 7)));

        commands.MoveCarets(id, CaretMove.LineStart);
        Assert.Equal(At(0, 4), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.LineStart);
        Assert.Equal(At(0, 0), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.LineStart);
        Assert.Equal(At(0, 4), Primary(editor, id));
    }

    [Fact]
    public async Task End_GoesToTheEndOfTheLine()
    {
        var (editor, id, commands) = await Create("abc\ndefgh", Caret.At(At(1, 1)));
        commands.MoveCarets(id, CaretMove.LineEnd);
        Assert.Equal(At(1, 5), Primary(editor, id));
    }

    [Fact]
    public async Task DocumentStartAndEnd()
    {
        var (editor, id, commands) = await Create("abc\ndef", Caret.At(At(0, 1)));

        commands.MoveCarets(id, CaretMove.DocumentEnd);
        Assert.Equal(At(1, 3), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.DocumentStart);
        Assert.Equal(At(0, 0), Primary(editor, id));
    }

    [Fact]
    public async Task ShiftEnd_SelectsToTheEndOfTheLine()
    {
        var (editor, id, commands) = await Create("hello world", Caret.At(At(0, 6)));

        commands.MoveCarets(id, CaretMove.LineEnd, extendSelection: true);

        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(0, 6), caret.Anchor);
        Assert.Equal(At(0, 11), caret.Position);
    }

    // ---------- Mots ----------

    [Fact]
    public async Task WordRight_AndWordLeft_JumpOverWords()
    {
        var (editor, id, commands) = await Create("foo bar", Caret.At(At(0, 0)));

        commands.MoveCarets(id, CaretMove.WordRight);
        Assert.Equal(At(0, 3), Primary(editor, id));
        commands.MoveCarets(id, CaretMove.WordRight);
        Assert.Equal(At(0, 7), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.WordLeft);
        Assert.Equal(At(0, 4), Primary(editor, id));
        commands.MoveCarets(id, CaretMove.WordLeft);
        Assert.Equal(At(0, 0), Primary(editor, id));
    }

    [Fact]
    public async Task WordRight_TreatsPunctuationAsItsOwnWord()
    {
        var (editor, id, commands) = await Create("foo.bar", Caret.At(At(0, 0)));

        commands.MoveCarets(id, CaretMove.WordRight);
        Assert.Equal(At(0, 3), Primary(editor, id));
        commands.MoveCarets(id, CaretMove.WordRight);
        Assert.Equal(At(0, 4), Primary(editor, id));
        commands.MoveCarets(id, CaretMove.WordRight);
        Assert.Equal(At(0, 7), Primary(editor, id));
    }

    [Fact]
    public async Task WordRight_AtLineEnd_GoesToTheNextLine_AndWordLeft_AtLineStart_ToThePreviousEnd()
    {
        var (editor, id, commands) = await Create("ab\ncd", Caret.At(At(0, 2)));

        commands.MoveCarets(id, CaretMove.WordRight);
        Assert.Equal(At(1, 0), Primary(editor, id));

        commands.MoveCarets(id, CaretMove.WordLeft);
        Assert.Equal(At(0, 2), Primary(editor, id));
    }

    // ---------- Plusieurs curseurs ----------

    [Fact]
    public async Task MoveCarets_MovesEveryCaret()
    {
        var (editor, id, commands) = await Create("abc\ndef", Caret.At(At(0, 0)), Caret.At(At(1, 0)));

        commands.MoveCarets(id, CaretMove.Right);

        var carets = editor.GetCaretSet(id);
        Assert.Equal(At(0, 1), carets.Carets[0].Position);
        Assert.Equal(At(1, 1), carets.Carets[1].Position);
    }

    [Fact]
    public async Task Carets_ThatMeet_AreMerged()
    {
        var (editor, id, commands) = await Create("ab", Caret.At(At(0, 0)), Caret.At(At(0, 1)));

        commands.MoveCarets(id, CaretMove.Left);

        Assert.Equal(1, editor.GetCaretSet(id).Count);
        Assert.Equal(At(0, 0), Primary(editor, id));
    }

    // ---------- Souris ----------

    [Fact]
    public async Task SetCaret_CollapsesToASingleCaret()
    {
        var (editor, id, commands) = await Create("abc\ndef", Caret.At(At(0, 0)), Caret.At(At(1, 0)));

        commands.SetCaret(id, At(1, 2));

        Assert.Equal(1, editor.GetCaretSet(id).Count);
        Assert.Equal(At(1, 2), Primary(editor, id));
    }

    [Fact]
    public async Task SetCaret_WithShift_ExtendsFromTheAnchor()
    {
        var (editor, id, commands) = await Create("abcdef", Caret.At(At(0, 1)));

        commands.SetCaret(id, At(0, 4), extendSelection: true);

        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(0, 1), caret.Anchor);
        Assert.Equal(At(0, 4), caret.Position);
    }

    [Fact]
    public async Task SetCaret_BeyondTheLineEnd_IsClamped()
    {
        var (editor, id, commands) = await Create("abc", Caret.At(At(0, 0)));

        commands.SetCaret(id, At(0, 99));

        Assert.Equal(At(0, 3), Primary(editor, id));
    }

    [Fact]
    public async Task AddCaret_AddsOne_AndAddingAgainOnTheSamePositionRemovesIt()
    {
        var (editor, id, commands) = await Create("abc\ndef", Caret.At(At(0, 0)));

        commands.AddCaret(id, At(1, 1));
        Assert.Equal(2, editor.GetCaretSet(id).Count);

        commands.AddCaret(id, At(1, 1));
        Assert.Equal(1, editor.GetCaretSet(id).Count);
    }

    [Fact]
    public async Task AddCaret_NeverRemovesTheLastCaret()
    {
        var (editor, id, commands) = await Create("abc", Caret.At(At(0, 1)));

        commands.AddCaret(id, At(0, 1));

        Assert.Equal(1, editor.GetCaretSet(id).Count);
    }

    [Fact]
    public async Task SelectWord_SelectsTheWordUnderThePosition()
    {
        var (editor, id, commands) = await Create("hello world", Caret.At(At(0, 0)));

        commands.SelectWord(id, At(0, 8));

        var caret = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(0, 6), caret.Anchor);
        Assert.Equal(At(0, 11), caret.Position);
    }

    [Fact]
    public async Task SelectWord_AtTheEndOfALine_SelectsTheLastWord()
    {
        var (editor, id, commands) = await Create("hello world", Caret.At(At(0, 0)));

        commands.SelectWord(id, At(0, 11));

        Assert.Equal(At(0, 6), editor.GetCaretSet(id).Primary.Anchor);
    }

    [Fact]
    public async Task SelectWord_OnAnEmptyLine_JustPlacesTheCaret()
    {
        var (editor, id, commands) = await Create("a\n\nb", Caret.At(At(0, 0)));

        commands.SelectWord(id, At(1, 0));

        Assert.False(editor.GetCaretSet(id).Primary.HasSelection);
        Assert.Equal(At(1, 0), Primary(editor, id));
    }

    [Fact]
    public async Task SelectLine_SelectsTheLineWithItsNewLine_AndTheLastLineWithoutOne()
    {
        var (editor, id, commands) = await Create("ab\ncd\nef", Caret.At(At(0, 0)));

        commands.SelectLine(id, At(1, 1));
        var middle = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(1, 0), middle.Anchor);
        Assert.Equal(At(2, 0), middle.Position);

        commands.SelectLine(id, At(2, 1));
        var last = editor.GetCaretSet(id).Primary;
        Assert.Equal(At(2, 0), last.Anchor);
        Assert.Equal(At(2, 2), last.Position);
    }

    // ---------- Curseurs au-dessus / en dessous ----------

    [Fact]
    public async Task AddCaretBelow_AddsACaretOnTheNextLine_ClampedToItsLength()
    {
        var (editor, id, commands) = await Create("abc\nde\nfghij", Caret.At(At(0, 3)));

        commands.AddCaretBelow(id);

        var carets = editor.GetCaretSet(id);
        Assert.Equal(2, carets.Count);
        Assert.Equal(At(0, 3), carets.Carets[0].Position);
        Assert.Equal(At(1, 2), carets.Carets[1].Position);
    }

    [Fact]
    public async Task AddCaretAbove_OnTheFirstLine_DoesNothing()
    {
        var (editor, id, commands) = await Create("abc", Caret.At(At(0, 1)));

        commands.AddCaretAbove(id);

        Assert.Equal(1, editor.GetCaretSet(id).Count);
    }

    [Fact]
    public async Task AddCaretAbove_StacksCarets_AndTypingEditsAllOfThem()
    {
        var (editor, id, commands) = await Create("a\nb\nc", Caret.At(At(2, 1)));

        commands.AddCaretAbove(id);
        commands.AddCaretAbove(id);
        commands.TypeText(id, "!");

        Assert.Equal("a!\nb!\nc!", editor.GetText(id));
    }
}
