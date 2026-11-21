using App.Core.Errors;
using App.Core.Models;

namespace App.Editor.Tests;

/// <summary>Accès ligne par ligne pour l'affichage (contrôle d'édition).</summary>
public sealed class LineAccessTests
{
    private static readonly string P = System.IO.Path.GetFullPath("/tmp/keyce-tests/lines.txt");

    private static async Task<(EditorService Svc, DocumentId Id)> Open(string text)
    {
        var files = new FakeFileService();
        files.Files[P] = text;
        var svc = new EditorService(files);
        return (svc, (await svc.OpenDocumentAsync(P)).Id);
    }

    [Fact]
    public async Task GetLineCount_AndGetLineText_ReturnLinesWithoutNewLines()
    {
        var (svc, id) = await Open("ab\r\ncd\nef");

        Assert.Equal(3, svc.GetLineCount(id));
        Assert.Equal("ab", svc.GetLineText(id, 0));
        Assert.Equal("cd", svc.GetLineText(id, 1));
        Assert.Equal("ef", svc.GetLineText(id, 2));
    }

    [Fact]
    public async Task EmptyDocument_HasOneEmptyLine()
    {
        var (svc, id) = await Open("");

        Assert.Equal(1, svc.GetLineCount(id));
        Assert.Equal("", svc.GetLineText(id, 0));
    }

    [Fact]
    public async Task Lines_FollowEdits()
    {
        var (svc, id) = await Open("ab");

        svc.ApplyEdit(id, new TextEdit(new TextRange(new TextPosition(0, 1), new TextPosition(0, 1)), "\nX"));

        Assert.Equal(2, svc.GetLineCount(id));
        Assert.Equal("a", svc.GetLineText(id, 0));
        Assert.Equal("Xb", svc.GetLineText(id, 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public async Task GetLineText_OutOfRange_IsRefused(int line)
    {
        var (svc, id) = await Open("ab\ncd");

        var ex = Assert.Throws<AppException>(() => svc.GetLineText(id, line));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }
}
