using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What the editor says about a <c>.place</c>: the module its path names, where that module is
/// declared, how the path is coloured, and the fix for placing a module that stands alone.
/// </summary>
public sealed class PlacementRequestsTests
{
    private const string MainUri = "file:///c:/work/main.nt65";
    private const string PartUri = "file:///c:/work/part.nt65";

    private const string Main = ".module main\n\n.segment CODE\n.export .proc start {\n    rts\n}\n\n.place part\n";

    /// <summary>
    /// A module is no symbol, so the path a <c>.place</c> writes is answered for itself: what
    /// the module's declaration says, whose output it is written in, and where it is declared.
    /// </summary>
    [Fact]
    public void ThePathAPlaceWritesNamesTheModule()
    {
        var (analysis, model) = Analyzed(Main, ".module part: placed\n\n.segment CODE\n.export .proc tail {\n    rts\n}\n");
        var at = Main.IndexOf("part", StringComparison.Ordinal) + 1;

        var hover = Lsp.ToHover(analysis, model, at);
        Assert.NotNull(hover);
        var text = hover.Contents.Value;
        Assert.Contains("module part", text, StringComparison.Ordinal);
        Assert.Contains("placed: its bytes are written where it is placed", text, StringComparison.Ordinal);
        Assert.Contains("the output of `main`", text, StringComparison.Ordinal);

        var definition = Lsp.ToPlacedDefinition(analysis, model, at);
        Assert.NotNull(definition);
        Assert.EndsWith("part.nt65", definition.Uri, StringComparison.Ordinal);
        Assert.Equal(new Range(new Position(0, 8), new Position(0, 12)), definition.Range);
    }

    /// <summary>The path is coloured as a module's, which the grammar cannot tell from a name.</summary>
    [Fact]
    public void ThePathAPlaceWritesIsColouredAsAModule()
    {
        var (_, model) = Analyzed(Main, ".module part: placed\n");
        var data = NameHighlighting.In(model).Data;
        var namespaceType = NameHighlighting.Legend.TokenTypes.ToList().IndexOf("namespace");

        // The tokens are five numbers each, the fourth of which is the type; the last is the path.
        Assert.Equal([4, 7, 4, namespaceType, 0], data.TakeLast(5));
    }

    /// <summary>
    /// Placing a module that says nothing about being placed is refused where the <c>.place</c>
    /// is, and the fix is written where the module is declared: it marks it <c>placed</c>.
    /// </summary>
    [Fact]
    public void PlacingAModuleThatStandsAloneOffersToDeclareItPlaced()
    {
        const string Part = ".module part\n\n.segment CODE\n.export .proc tail {\n    rts\n}\n";
        var (analysis, model) = Analyzed(Main, Part);

        var refused = Assert.Single(analysis.DiagnosticsFor(model.Tree.Path));
        Assert.Equal("place-not-placeable", refused.Id);
        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Kind == "quickfix");
        Assert.Equal("Declare `part` as placed", action.Title);
        Assert.Equal([PartUri], action.Edit.Changes.Keys);
        Assert.Equal(".module part: placed\n" + Part[".module part\n".Length..], Editing.Apply(Part, action.Edit.Changes[PartUri]));
    }

    /// <summary>The whole file, which is what a client asks about when it asks about all of it.</summary>
    private static Range Whole => new(new Position(0, 0), new Position(1000, 0));

    private static (ProgramAnalysis Analysis, SemanticModel Model) Analyzed(string main, string part)
    {
        var workspace = new Workspace();
        workspace.Open(new TextDocumentItem(PartUri, "nt65", 1, part));
        var document = workspace.Open(new TextDocumentItem(MainUri, "nt65", 1, main));
        var analysis = workspace.AnalysisFor(document.Tree.Path);
        return (analysis, analysis.ModelFor(document.Tree.Path)!);
    }
}
