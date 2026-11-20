using App.Editor;
using App.Editor.Editing;

namespace App.Editor.Tests;

public class SearchReplaceServiceTests
{
    private readonly SearchReplaceService _service = new();

    [Fact]
    public void FindAll_ReturnsAllOccurrences()
    {
        var document = new TextModel("cat dog cat cat");

        var results = _service.FindAll(document, "cat");

        Assert.Equal(3, results.Count);
        Assert.Equal(
            new[] { 0, 8, 12 },
            results.Select(x => x.Offset));
    }

    [Fact]
    public void FindAll_IsCaseInsensitiveByDefault()
    {
        var document = new TextModel("Hello hello HELLO");

        var results = _service.FindAll(document, "hello");

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void FindAll_CanMatchCase()
    {
        var document = new TextModel("Hello hello HELLO");

        var results = _service.FindAll(
            document,
            "hello",
            new SearchOptions(MatchCase: true));

        Assert.Single(results);
        Assert.Equal(6, results[0].Offset);
    }

    [Fact]
    public void FindAll_CanMatchWholeWords()
    {
        var document = new TextModel("cat category cat");

        var results = _service.FindAll(
            document,
            "cat",
            new SearchOptions(WholeWord: true));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void FindAll_SupportsRegex()
    {
        var document = new TextModel("abc 123 def 456");

        var results = _service.FindAll(
            document,
            @"\d+",
            new SearchOptions(UseRegex: true));

        Assert.Equal(2, results.Count);
        Assert.Equal("123", results[0].Value);
        Assert.Equal("456", results[1].Value);
    }

    [Fact]
    public void FindNext_FindsNextOccurrence()
    {
        var document = new TextModel("one two one");

        var result = _service.FindNext(
            document, "one", 1);

        Assert.NotNull(result);
        Assert.Equal(8, result.Offset);
    }

    [Fact]
    public void FindNext_CanWrapAround()
    {
        var document = new TextModel("one two one");

        var result = _service.FindNext(
            document,
            "one",
            9,
            new SearchOptions(WrapAround: true));

        Assert.NotNull(result);
        Assert.Equal(0, result.Offset);
    }

    [Fact]
    public void FindPrevious_FindsPreviousOccurrence()
    {
        var document = new TextModel("one two one");

        var result = _service.FindPrevious(
            document, "one", 10);

        Assert.NotNull(result);
        Assert.Equal(8, result.Offset);
    }

    [Fact]
    public void CreateReplaceEdit_CreatesCorrectEdit()
    {
        var document = new TextModel("Hello world");

        var match = Assert.Single(
            _service.FindAll(document, "world"));

        var edit = _service.CreateReplaceEdit(
            document, match, "Keyce");

        Assert.NotNull(edit);
        Assert.Equal("Keyce", edit.NewText);
        Assert.Equal(6, edit.Range.Start.Column);
        Assert.Equal(11, edit.Range.End.Column);
    }

    [Fact]
    public void CreateReplaceAllEdits_ReturnsDescendingOffsets()
    {
        var document = new TextModel("cat dog cat");

        var edits = _service.CreateReplaceAllEdits(
            document, "cat", "fox");

        Assert.Equal(2, edits.Count);

        Assert.Equal(8, edits[0].Range.Start.Column);
        Assert.Equal(0, edits[1].Range.Start.Column);
    }

    [Fact]
    public void CreateReplaceAllEdits_SupportsRegexGroups()
    {
        var document = new TextModel("user123");

        var edits = _service.CreateReplaceAllEdits(
            document,
            @"user(\d+)",
            "account-$1",
            new SearchOptions(UseRegex: true));

        Assert.Single(edits);
        Assert.Equal("account-123", edits[0].NewText);
    }

    [Fact]
    public void EmptyPattern_ReturnsNoMatches()
    {
        var document = new TextModel("Hello");

        var results = _service.FindAll(document, "");

        Assert.Empty(results);
    }
}