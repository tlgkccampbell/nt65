using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Syntax;
using Norristown.Tests.Syntax;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>Tests the workspace as a document cache, including what an edit costs and where the text ends up.</summary>
public sealed class WorkspaceTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = ".module main\n.proc reset {\n    ldx #0\n@loop:\n    sta $0200,x\n    rts\n}\n";

    /// <summary>
    /// <see cref="Uri"/> as a path, which is the same on every host. The workspace treats
    /// <c>c:</c> as a drive on any host, because to the analysis a path is only a name, and only
    /// the editor's URI has to come back unchanged.
    /// </summary>
    private const string Named = "c:/work/main.nt65";

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
        Assert.Equal(Named, Workspace.PathOf(Uri));
        Assert.Equal("untitled:Untitled-1", Workspace.PathOf("untitled:Untitled-1"));
    }

    /// <summary>
    /// A path converts back to a URI the same way on every host. .NET does not read a rooted path
    /// as a URI unless it starts with a drive letter, and on Linux every path a workspace holds
    /// starts with a <c>/</c>, including a path that holds a Windows editor's drive letter. So a
    /// path the analysis uses to name another file has to be converted to a URI rather than
    /// passed bare.
    /// </summary>
    [Fact]
    public void ARootedPathComesBackAsAFileUriOnEveryHost()
    {
        Assert.Equal("file:///home/u/p/main.nt65", Lsp.ToUri("/home/u/p/main.nt65"));
        Assert.Equal("file:///c:/work/main.nt65", Lsp.ToUri("/c:/work/main.nt65"));

        // Characters a URI cannot hold are escaped in any file name.
        Assert.Equal("file:///home/u/my%20file.nt65", Lsp.ToUri("/home/u/my file.nt65"));
        Assert.Equal("file:///home/u/a%23b.nt65", Lsp.ToUri("/home/u/a#b.nt65"));

        // An untitled document and a file the editor named relatively are not paths, so they
        // come back as they went in.
        Assert.Equal("untitled:Untitled-1", Lsp.ToUri("untitled:Untitled-1"));
        Assert.Equal("main.nt65", Lsp.ToUri("main.nt65"));
    }

    /// <summary>
    /// The program is analyzed with the editor's active configuration. Its defines override the
    /// project's, and a configuration name the project does not have is reported.
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
            var main = Workspace.PathOf(new Uri(Path.Combine(root.FullName, "main.nt65")).AbsoluteUri);
            var workspace = new Workspace();

            workspace.Load(uri);
            Assert.Empty(workspace.AnalysisFor(main).Diagnostics);

            workspace.Load(uri, "debug");
            Assert.Equal(["built for debugging"], workspace.AnalysisFor(main).Diagnostics.Select(d => d.Message));

            workspace.Load(uri, "ntsc");
            Assert.Equal(["`ntsc` is not a configuration: nt65.json names `debug`"], workspace.AnalysisFor(main).Diagnostics.Select(d => d.Message));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// An edit re-lexes the lines it touches and no others. The lines above and below keep the
    /// green nodes they had, which is the reuse the tree is built for.
    /// </summary>
    [Fact]
    public void AnEditReusesTheGreenNodesItDidNotTouch()
    {
        var workspace = OpenSource(out var opened);
        var changed = workspace.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(Locate.Span(Source, "ldx #|0"), "1")]);

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

    /// <summary>After an incremental edit of any shape, the tree is the one a fresh parse would give.</summary>
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

    /// <summary>
    /// A change with no range replaces the whole document. A client falls back to that form when
    /// it does not use incremental edits.
    /// </summary>
    [Fact]
    public void AChangeWithNoRangeReplacesTheWholeDocument()
    {
        var workspace = OpenSource(out _);
        var changed = workspace.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(null, "nop\n")]);

        Assert.NotNull(changed);
        Assert.Equal("nop\n", changed.Tree.Text);
        Assert.Equal(Named, changed.Tree.Path);
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
