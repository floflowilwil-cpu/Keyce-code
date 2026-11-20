using App.Core.Models;

namespace App.Editor.Tests;

public sealed class SelectionModelsTests
{
    [Fact]
    public void Selection_NormalizesRange_WithoutLosingDirection()
    {
        var selection = new Selection(new TextPosition(2, 4), new TextPosition(1, 3));

        Assert.True(selection.IsReversed);
        Assert.Equal(new TextRange(new TextPosition(1, 3), new TextPosition(2, 4)), selection.Range);
        Assert.Equal(new TextPosition(2, 4), selection.CollapseToAnchor().Active);
        Assert.Equal(new TextPosition(1, 3), selection.CollapseToActive().Active);
    }

    [Fact]
    public void Caret_CanExtendOrCollapseSelection()
    {
        var caret = Caret.At(new TextPosition(0, 2));
        var extended = caret.MoveTo(new TextPosition(0, 6), extendSelection: true);

        Assert.True(extended.HasSelection);
        Assert.Equal(new TextPosition(0, 2), extended.Anchor);
        Assert.Equal(new TextPosition(0, 6), extended.Position);

        var moved = extended.MoveTo(new TextPosition(1, 1));
        Assert.False(moved.HasSelection);
        Assert.Equal(new TextPosition(1, 1), moved.Anchor);
    }

    [Fact]
    public void CaretSet_PreservesPrimaryAndInputOrder()
    {
        var first = Caret.At(new TextPosition(0, 1));
        var second = Caret.At(new TextPosition(2, 3));
        var set = new CaretSet([first, second], primaryIndex: 1);

        Assert.Equal(2, set.Count);
        Assert.Equal(1, set.PrimaryIndex);
        Assert.Equal(second, set.Primary);
        Assert.Equal(first, set.Carets[0]);
    }

    [Fact]
    public void CaretSet_AddAndRemove_KeepValidPrimaryIndex()
    {
        var set = new CaretSet([
            Caret.At(new TextPosition(0, 0)),
            Caret.At(new TextPosition(1, 0))
        ], primaryIndex: 1);

        var added = set.Add(Caret.At(new TextPosition(2, 0)));
        Assert.Equal(2, added.PrimaryIndex);

        var removed = added.RemoveAt(0);
        Assert.Equal(1, removed.PrimaryIndex);
        Assert.Equal(new TextPosition(2, 0), removed.Primary.Position);
    }

    [Fact]
    public void CaretSet_RequiresAtLeastOneCaret()
    {
        Assert.Throws<ArgumentException>(() => new CaretSet([]));
    }

    [Fact]
    public void CaretSet_RejectsDuplicateActivePositions()
    {
        var position = new TextPosition(0, 2);
        Assert.Throws<ArgumentException>(() => new CaretSet([
            Caret.At(position),
            Caret.At(position)
        ]));
    }

    [Fact]
    public void CaretSet_RejectsOverlappingSelections()
    {
        Assert.Throws<ArgumentException>(() => new CaretSet([
            Caret.FromSelection(new Selection(new TextPosition(0, 1), new TextPosition(0, 5))),
            Caret.FromSelection(new Selection(new TextPosition(0, 4), new TextPosition(0, 8)))
        ]));
    }

    [Fact]
    public void CaretSet_AllowsTouchingSelections()
    {
        var set = new CaretSet([
            Caret.FromSelection(new Selection(new TextPosition(0, 1), new TextPosition(0, 3))),
            Caret.FromSelection(new Selection(new TextPosition(0, 3), new TextPosition(0, 5)))
        ]);

        Assert.Equal(2, set.Count);
    }
}
