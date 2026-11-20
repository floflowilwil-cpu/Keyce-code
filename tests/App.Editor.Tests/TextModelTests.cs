using App.Core.Errors;
using App.Core.Models;


namespace App.Editor.Tests;

public class TextModelTests
{
    [Fact]
    public void EmptyBuffer_HasOneEmptyLine()
    {
        var b = new TextModel();
        Assert.Equal(0, b.Length);
        Assert.Equal(1, b.LineCount);
        Assert.Equal("", b.GetLineText(0));
    }

    [Fact]
    public void Constructor_NormalizesNewLines()
    {
        var b = new TextModel("a\r\nb\rc\nd");
        Assert.Equal("a\nb\nc\nd", b.GetText());
        Assert.Equal(4, b.LineCount);
    }

    [Fact]
    public void Lines_AreSplitCorrectly()
    {
        var b = new TextModel("hello\n\nworld\n");
        Assert.Equal(4, b.LineCount);
        Assert.Equal("hello", b.GetLineText(0));
        Assert.Equal("", b.GetLineText(1));
        Assert.Equal("world", b.GetLineText(2));
        Assert.Equal("", b.GetLineText(3));
    }

    [Fact]
    public void Insert_InMiddle_UpdatesTextAndLines()
    {
        var b = new TextModel("ac");
        b.Insert(1, "b");
        Assert.Equal("abc", b.GetText());
        b.Insert(3, "\ndef\nghi");
        Assert.Equal("abc\ndef\nghi", b.GetText());
        Assert.Equal(3, b.LineCount);
        Assert.Equal("def", b.GetLineText(1));
    }

    [Fact]
    public void Delete_AcrossLines_MergesLines()
    {
        var b = new TextModel("one\ntwo\nthree");
        b.Delete(2, 7); // "e\ntwo\nt" -> reste "on" + "hree"
        Assert.Equal("onhree", b.GetText());
        Assert.Equal(1, b.LineCount);
    }

    [Fact]
    public void Replace_WorksAsDeletePlusInsert()
    {
        var b = new TextModel("hello world");
        b.Replace(6, 5, "there");
        Assert.Equal("hello there", b.GetText());
    }

    [Fact]
    public void PositionAndOffset_AreInverse()
    {
        var b = new TextModel("ab\ncde\n\nf");
        for (int off = 0; off <= b.Length; off++)
            Assert.Equal(off, b.GetOffset(b.GetPosition(off)));
        Assert.Equal(new TextPosition(1, 2), b.GetPosition(5));
    }

    [Fact]
    public void GetOffset_ClampsLikeLsp()
    {
        var b = new TextModel("ab\ncd");
        Assert.Equal(2, b.GetOffset(new TextPosition(0, 99)));   // colonne trop grande -> fin de ligne
        Assert.Equal(5, b.GetOffset(new TextPosition(99, 0)));   // ligne trop grande -> fin du document
    }

    [Fact]
    public void NegativePosition_Throws()
    {
        var b = new TextModel("abc");
        var ex = Assert.Throws<AppException>(() => b.GetOffset(new TextPosition(-1, 0)));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void OutOfRangeDelete_Throws_AndLeavesBufferUntouched()
    {
        var b = new TextModel("abc");
        Assert.Throws<AppException>(() => b.Delete(2, 5));
        Assert.Equal("abc", b.GetText());
    }

    [Fact]
    public void GetText_Range()
    {
        var b = new TextModel("abc\ndef\nghi");
        var range = new TextRange(new TextPosition(0, 1), new TextPosition(2, 1));
        Assert.Equal("bc\ndef\ng", b.GetText(range));
    }

    [Fact]
    public void RandomEdits_MatchReferenceModel()
    {
        var rng = new Random(12345);
        var model = new System.Text.StringBuilder("start\nline2\n");
        var buffer = new TextModel(model.ToString());
        string[] pool = ["a", "xyz", "\n", "ab\ncd", "\n\n", "é", ""];

        for (int step = 0; step < 3000; step++)
        {
            int offset = rng.Next(model.Length + 1);
            int len = rng.Next(0, Math.Min(6, model.Length - offset) + 1);
            string text = pool[rng.Next(pool.Length)];

            model.Remove(offset, len).Insert(offset, text);
            buffer.Replace(offset, len, text);

            if (step % 25 != 0) continue;
            string expected = model.ToString();
            Assert.Equal(expected, buffer.GetText());
            string[] lines = expected.Split('\n');
            Assert.Equal(lines.Length, buffer.LineCount);
            for (int l = 0; l < lines.Length; l++)
                Assert.Equal(lines[l], buffer.GetLineText(l));
        }
    }
}
