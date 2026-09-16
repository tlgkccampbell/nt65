using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Syntax;
using Norristown.Tests.Syntax;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>The open-document cache: what an edit costs, and where the text ends up.</summary>
public sealed class DocumentsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = ".proc reset {\n    ldx #0\n@loop:\n    sta $0200,x\n    rts\n}\n";

    private static Documents OpenSource(out Document document)
    {
        var documents = new Documents();
        document = documents.Open(new TextDocumentItem(Uri, "nt65", 1, Source));
        return documents;
    }

    /// <summary>The URI the editor uses becomes the path diagnostics and output carry.</summary>
    [Fact]
    public void ADocumentsPathComesFromItsUri()
    {
        Assert.Equal("c:/work/main.nt65", Documents.PathOf(Uri));
        Assert.Equal("untitled:Untitled-1", Documents.PathOf("untitled:Untitled-1"));
    }

    /// <summary>
    /// An edit re-lexes the lines it touches and no others: the lines above and below keep
    /// the green nodes they had, which is the reuse Stage 2 built the tree for.
    /// </summary>
    [Fact]
    public void AnEditReusesTheGreenNodesItDidNotTouch()
    {
        var documents = OpenSource(out var opened);
        var changed = documents.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(new Range(new Position(1, 9), new Position(1, 10)), "1")]);

        Assert.NotNull(changed);
        Assert.Equal(2, changed.Version);
        Assert.Equal(Source.Replace("ldx #0", "ldx #1"), changed.Tree.Text);
        for (var line = 0; line < opened.Tree.Lines.Length; line++)
        {
            if (line == 1)
                Assert.NotSame(opened.Tree.Lines[line], changed.Tree.Lines[line]);
            else
                Assert.Same(opened.Tree.Lines[line], changed.Tree.Lines[line]);
        }
    }

    /// <summary>However the edit arrived, the tree is the one a fresh parse would give.</summary>
    [Theory]
    [InlineData(2, 0, 2, 6, "@again:")]     // replacing a whole label
    [InlineData(5, 1, 5, 1, "\n    nop\n")] // appending lines past the last `}`
    [InlineData(0, 0, 6, 0, "")]            // deleting everything
    [InlineData(3, 8, 3, 13, "")]           // shortening an operand
    public void AnIncrementalEditMatchesAFreshParse(int startLine, int startCharacter, int endLine, int endCharacter, string text)
    {
        var documents = OpenSource(out _);
        var changed = documents.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(
                new Range(new Position(startLine, startCharacter), new Position(endLine, endCharacter)), text)]);

        Assert.NotNull(changed);
        var fresh = SyntaxTree.Parse(changed.Tree.Path, changed.Tree.Text);
        Assert.Equal(SyntaxDump.Full(fresh), SyntaxDump.Full(changed.Tree));
    }

    /// <summary>A change with no range replaces the document, which is what a client falls back to.</summary>
    [Fact]
    public void AChangeWithNoRangeReplacesTheWholeDocument()
    {
        var documents = OpenSource(out _);
        var changed = documents.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(null, "nop\n")]);

        Assert.NotNull(changed);
        Assert.Equal("nop\n", changed.Tree.Text);
        Assert.Equal("c:/work/main.nt65", changed.Tree.Path);
    }

    /// <summary>A position past the end of a line or the file is clamped, not an error.</summary>
    [Fact]
    public void PositionsOutsideTheDocumentAreClamped()
    {
        var documents = OpenSource(out _);
        var changed = documents.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(new Range(new Position(99, 0), new Position(99, 4)), "nop\n")]);

        Assert.NotNull(changed);
        Assert.Equal(Source + "nop\n", changed.Tree.Text);
    }

    [Fact]
    public void ChangingADocumentThatIsNotOpenIsIgnored()
    {
        var documents = new Documents();
        Assert.Null(documents.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(null, "nop\n")]));
        Assert.Null(documents.Find(Uri));
    }

    [Fact]
    public void ClosingADocumentForgetsIt()
    {
        var documents = OpenSource(out _);
        Assert.NotNull(documents.Find(Uri));
        documents.Close(Uri);
        Assert.Null(documents.Find(Uri));
    }
}
