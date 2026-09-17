using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Syntax;
using Norristown.Tests.Syntax;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>The workspace as a document cache: what an edit costs, and where the text ends up.</summary>
public sealed class WorkspaceTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = ".module main\n.proc reset {\n    ldx #0\n@loop:\n    sta $0200,x\n    rts\n}\n";

    private static Workspace OpenSource(out Document document)
    {
        var workspace = new Workspace();
        document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Source));
        return workspace;
    }

    /// <summary>The URI the editor uses becomes the path diagnostics and output carry.</summary>
    [Fact]
    public void AWorkspacePathComesFromItsUri()
    {
        Assert.Equal("c:/work/main.nt65", Workspace.PathOf(Uri));
        Assert.Equal("untitled:Untitled-1", Workspace.PathOf("untitled:Untitled-1"));
    }

    /// <summary>
    /// The editor's active configuration is the one the program is analyzed as: its defines over
    /// the project's, and a name the project does not have is reported.
    /// </summary>
    [Fact]
    public void TheActiveConfigurationIsWhatTheProgramIsAnalyzedAs()
    {
        var root = Directory.CreateTempSubdirectory("nt65-workspace-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "nt65.json"),
                """{ "cpu": "6502", "files": ["*.nt65"], "defines": { "DEBUG": 0 }, "configurations": { "debug": { "defines": { "DEBUG": 1 } } } }""");
            File.WriteAllText(Path.Combine(root.FullName, "main.nt65"), ".module main\n.if DEBUG {\n    .error \"built for debugging\"\n}\n");
            var uri = new Uri(root.FullName).AbsoluteUri;
            var workspace = new Workspace();

            workspace.Load(uri);
            Assert.Empty(workspace.Analysis().Diagnostics);

            workspace.Load(uri, "debug");
            Assert.Equal(["built for debugging"], workspace.Analysis().Diagnostics.Select(d => d.Message));

            workspace.Load(uri, "ntsc");
            Assert.Equal(["`ntsc` is not a configuration: nt65.json names `debug`"], workspace.Analysis().Diagnostics.Select(d => d.Message));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// An edit re-lexes the lines it touches and no others: the lines above and below keep
    /// the green nodes they had, which is the reuse the tree is built for.
    /// </summary>
    [Fact]
    public void AnEditReusesTheGreenNodesItDidNotTouch()
    {
        var workspace = OpenSource(out var opened);
        var changed = workspace.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(new Range(new Position(2, 9), new Position(2, 10)), "1")]);

        Assert.NotNull(changed);
        Assert.Equal(2, changed.Version);
        Assert.Equal(Source.Replace("ldx #0", "ldx #1"), changed.Tree.Text);
        for (var line = 0; line < opened.Tree.Lines.Length; line++)
        {
            if (line == 2)
                Assert.NotSame(opened.Tree.Lines[line], changed.Tree.Lines[line]);
            else
                Assert.Same(opened.Tree.Lines[line], changed.Tree.Lines[line]);
        }
    }

    /// <summary>However the edit arrived, the tree is the one a fresh parse would give.</summary>
    [Theory]
    [InlineData(3, 0, 3, 6, "@again:")]     // replacing a whole label
    [InlineData(6, 1, 6, 1, "\n    nop\n")] // appending lines past the last `}`
    [InlineData(0, 0, 7, 0, "")]            // deleting everything
    [InlineData(4, 8, 4, 13, "")]           // shortening an operand
    public void AnIncrementalEditMatchesAFreshParse(int startLine, int startCharacter, int endLine, int endCharacter, string text)
    {
        var workspace = OpenSource(out _);
        var changed = workspace.Change(new VersionedTextDocumentIdentifier(Uri, 2),
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
        var workspace = OpenSource(out _);
        var changed = workspace.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(null, "nop\n")]);

        Assert.NotNull(changed);
        Assert.Equal("nop\n", changed.Tree.Text);
        Assert.Equal("c:/work/main.nt65", changed.Tree.Path);
    }

    /// <summary>A position past the end of a line or the file is clamped, not an error.</summary>
    [Fact]
    public void PositionsOutsideTheDocumentAreClamped()
    {
        var workspace = OpenSource(out _);
        var changed = workspace.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(new Range(new Position(100, 0), new Position(100, 4)), "nop\n")]);

        Assert.NotNull(changed);
        Assert.Equal(Source + "nop\n", changed.Tree.Text);
    }

    [Fact]
    public void ChangingADocumentThatIsNotOpenIsIgnored()
    {
        var workspace = new Workspace();
        Assert.Null(workspace.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(null, "nop\n")]));
        Assert.Null(workspace.Find(Uri));
    }

    [Fact]
    public void ClosingADocumentForgetsIt()
    {
        var workspace = OpenSource(out _);
        Assert.NotNull(workspace.Find(Uri));
        workspace.Close(Uri);
        Assert.Null(workspace.Find(Uri));
    }
}
