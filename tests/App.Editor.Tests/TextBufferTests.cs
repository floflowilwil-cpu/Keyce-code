
using App.Editor;

namespace App.Editor.Tests;

public class TextBufferTests
{
    [Fact]
    public void Constructor_ShouldStoreInitialText()
    {
        var buffer = new TextBuffer("Bonjour");

        Assert.Equal("Bonjour", buffer.GetText());
        Assert.Equal(7, buffer.Length);
    }

    [Fact]
    public void Constructor_ShouldSupportEmptyText()
    {
        var buffer = new TextBuffer();

        Assert.True(buffer.IsEmpty);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Insert_ShouldAddTextAtBeginning()
    {
        var buffer = new TextBuffer("Monde");

        buffer.Insert(0, "Bonjour ");

        Assert.Equal("Bonjour Monde", buffer.GetText());
    }

    [Fact]
    public void Insert_ShouldAddTextInMiddle()
    {
        var buffer = new TextBuffer("Bonjour");

        buffer.Insert(3, "XYZ");

        Assert.Equal("BonXYZjour", buffer.GetText());
    }

    [Fact]
    public void Insert_ShouldAddTextAtEnd()
    {
        var buffer = new TextBuffer("Bonjour");

        buffer.Insert(7, " !");

        Assert.Equal("Bonjour !", buffer.GetText());
    }

    [Fact]
    public void Insert_ShouldSupportEmptyBuffer()
    {
        var buffer = new TextBuffer();

        buffer.Insert(0, "Keyce");

        Assert.Equal("Keyce", buffer.GetText());
    }

    [Fact]
    public void Delete_ShouldRemoveRequestedText()
    {
        var buffer = new TextBuffer("Bonjour Azra");

        buffer.Delete(8, 4);

        Assert.Equal("Bonjour ", buffer.GetText());
    }

    [Fact]
    public void Delete_ShouldRemoveTextAcrossPieces()
    {
        var buffer = new TextBuffer("ABCDEF");

        buffer.Insert(3, "123");

        buffer.Delete(2, 3);

        Assert.Equal("AB3DEF", buffer.GetText());
    }

    [Fact]
    public void Replace_ShouldReplaceRequestedText()
    {
        var buffer = new TextBuffer("Bonjour Monde");

        buffer.Replace(8, 5, "Azra");

        Assert.Equal("Bonjour Azra", buffer.GetText());
    }

    [Fact]
    public void GetText_ShouldReturnRequestedRange()
    {
        var buffer = new TextBuffer("ABCDEFGHIJ");

        Assert.Equal("CDEF", buffer.GetText(2, 4));
    }

    [Fact]
    public void Delete_ShouldHandleEmptyDocument()
    {
        var buffer = new TextBuffer("Bonjour");

        buffer.Delete(0, 7);

        Assert.True(buffer.IsEmpty);
        Assert.Equal("", buffer.GetText());
    }

    [Fact]
    public void Insert_ShouldRejectInvalidPosition()
    {
        var buffer = new TextBuffer("Bonjour");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Insert(-1, "Test")
        );
    }

    [Fact]
    public void Delete_ShouldRejectInvalidRange()
    {
        var buffer = new TextBuffer("Bonjour");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Delete(3, 10)
        );
    }

    [Fact]
    public void MultipleOperations_ShouldPreserveText()
    {
        var buffer = new TextBuffer("ABCDE");

        buffer.Insert(2, "123");
        buffer.Delete(1, 2);
        buffer.Replace(2, 2, "XYZ");

        Assert.Equal("A2XYZDE", buffer.GetText());
    }

    [Fact]
    public void InsertEmptyText_ShouldNotModifyDocument()
    {
        var buffer = new TextBuffer("Bonjour");

        buffer.Insert(3, "");

        Assert.Equal("Bonjour", buffer.GetText());
    }

    [Fact]
    public void DeleteZeroCharacters_ShouldNotModifyDocument()
    {
        var buffer = new TextBuffer("Bonjour");

        buffer.Delete(3, 0);

        Assert.Equal("Bonjour", buffer.GetText());
    }
}
