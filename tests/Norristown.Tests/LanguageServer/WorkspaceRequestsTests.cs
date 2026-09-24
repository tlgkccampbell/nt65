using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what an editor gets across modules. An exported name is the same symbol in every module
/// that uses it, so definition, references and rename follow it into those modules through the
/// <c>.use</c> that brings it in. An edit in one module changes the diagnostics of another.
/// </summary>
public sealed class WorkspaceRequestsTests
{
    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string MainUri = "file:///c:/work/main.nt65";

    private const string Gfx = """
        .module gfx
        .export clear, SCREEN

        .const SCREEN = $0400
        .const rows   = 25
        .segment CODE
        .proc clear {
            rts
        }
        """;

    private const string Main = """
        .module main
        .use gfx::{clear, SCREEN}
        .segment CODE
        .proc main {
            jsr clear
            lda #<SCREEN
            rts
        }
        """;

    [Fact]
    public async Task DefinitionCrossesIntoTheModuleThatDeclaresTheName()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // `clear` on `jsr clear`, declared by gfx.nt65.
        var definition = await client.DefinitionAsync(MainUri, Locate.At(Main, "jsr |clear"), timeout);

        Assert.NotNull(definition);
        Assert.Equal(GfxUri, definition.Uri);
        Assert.Equal(Locate.Span(Gfx, ".proc |clear"), definition.Range);
    }

    /// <summary>Hover on a name from another module says which module it came from.</summary>
    [Fact]
    public async Task HoverNamesTheModuleANameComesFrom()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(MainUri, Locate.At(Main, "jsr |clear"), timeout);

        Assert.NotNull(hover);
        Assert.Contains("```nt65\n.proc gfx::clear\n```", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("from       gfx.nt65", hover.Contents.Value, StringComparison.Ordinal);

        // The hover shows what the call costs, worked out from the flow analysis of the file that
        // declares the routine rather than the file that contains the call. It is what a caller
        // hovers to find out, so it comes above the rule, and the routine's address comes below it.
        Assert.Contains(
            "from       gfx.nt65\ncost       6 cycles\nreads      none\npreserves  A, X, Y, C\n```\n---\n",
            hover.Contents.Value,
            StringComparison.Ordinal);
        Assert.Contains("address", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferencesSpanEveryModuleThatNamesTheSymbol()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // Starting from the declaration in gfx.nt65, the references are the `.export` and the
        // `.proc` there, and the `.use` and the call in main.
        var references = await client.ReferencesAsync(GfxUri, Locate.At(Gfx, ".proc |clear"), true, timeout);

        Assert.Equal([GfxUri, GfxUri, MainUri, MainUri], references.Select(r => r.Uri));
    }

    /// <summary>A rename across modules rewrites the <c>.use</c> that brings the name in.</summary>
    [Fact]
    public async Task RenamingAnExportedNameEditsEveryModule()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var edit = await client.RenameAsync(MainUri, Locate.At(Main, "jsr |clear"), "wipe", timeout);

        Assert.NotNull(edit);
        Assert.Equal([GfxUri, MainUri], edit.Changes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, edit.Changes[GfxUri].Count);
        Assert.Equal([1, 4], edit.Changes[MainUri].Select(change => change.Range.Start.Line));
        Assert.All(edit.Changes[MainUri], change => Assert.Equal("wipe", change.NewText));
    }

    /// <summary>
    /// An alias given by <c>.use ... as</c> belongs to the module that declares it. Renaming the
    /// original symbol leaves the alias alone, and renaming the alias changes only the alias.
    /// </summary>
    [Fact]
    public async Task ARenameKeepsAnAliasApartFromTheNameItStandsFor()
    {
        const string Text = ".module main\n.use gfx::clear as wipe\n.segment CODE\n.proc main {\n    jsr wipe\n    rts\n}\n";
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (GfxUri, Gfx));
        await client.OpenAsync(MainUri, Text);
        await NextForAsync(client, MainUri, timeout);

        var alias = await client.RenameAsync(MainUri, Locate.At(Text, "jsr |wipe"), "erase", timeout);
        Assert.NotNull(alias);
        Assert.Equal([MainUri], alias.Changes.Keys);
        Assert.Equal([1, 4], alias.Changes[MainUri].Select(change => change.Range.Start.Line));

        var symbol = await client.RenameAsync(GfxUri, Locate.At(Gfx, ".proc |clear"), "blank", timeout);
        Assert.NotNull(symbol);
        Assert.Equal(2, symbol.Changes[GfxUri].Count);
        var inMain = Assert.Single(symbol.Changes[MainUri]);
        Assert.Equal(Locate.Span(Text, "gfx::|clear"), inMain.Range);
    }

    /// <summary>A name another module keeps to itself is reported as private, not as missing.</summary>
    [Fact]
    public async Task NamingSomethingUnexportedIsReported()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (GfxUri, Gfx));
        await client.OpenAsync(MainUri, ".module main\n.const n = gfx::rows\n");

        var published = await NextForAsync(client, MainUri, timeout);
        Assert.Equal("`gfx::rows` is not exported by module `gfx`",
            Assert.Single(published.Diagnostics).Message);
    }

    /// <summary>
    /// An edit to one module republishes the others. Dropping an export makes the module that
    /// used the name wrong, and the editor has to report the error there.
    /// </summary>
    [Fact]
    public async Task AnEditInOneModuleChangesWhatIsWrongWithAnother()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // `.export clear, SCREEN` becomes `.export SCREEN`.
        await client.ChangeAsync(GfxUri, 2,
            new TextDocumentContentChangeEvent(Locate.Span(Gfx, ".export |clear, "), ""));

        var published = await NextForAsync(client, MainUri, timeout);
        Assert.Equal("`gfx::clear` is not exported by module `gfx`",
            Assert.Single(published.Diagnostics).Message);
    }

    /// <summary>
    /// An edit that does not change what other modules can see of a module reanalyzes only that
    /// file, so main.nt65 keeps its analysis from before the edit. Its names must still resolve to
    /// what gfx.nt65 declares now, wherever the edit moved those declarations.
    /// </summary>
    [Fact]
    public async Task NamesStillCrossModulesAfterAnEditOnlyOneFileWasAnalyzedFor()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // A comment line above `.proc clear`, which moves it down a line. Nothing about
        // main.nt65 changes, so gfx.nt65 is the file the server publishes for.
        var above = Locate.At(Gfx, ".proc clear");
        var edited = Gfx.Replace(".proc clear", "; wipes the screen\n.proc clear", StringComparison.Ordinal);
        await client.ChangeAsync(GfxUri, 2,
            new TextDocumentContentChangeEvent(new Range(above, above), "; wipes the screen\n"));
        await NextForAsync(client, GfxUri, timeout);

        var definition = await client.DefinitionAsync(MainUri, Locate.At(Main, "jsr |clear"), timeout);
        Assert.NotNull(definition);
        Assert.Equal(Locate.Span(edited, ".proc |clear"), definition.Range);

        var references = await client.ReferencesAsync(GfxUri, Locate.At(edited, ".proc |clear"), true, timeout);
        Assert.Equal([GfxUri, GfxUri, MainUri, MainUri], references.Select(r => r.Uri));
    }

    /// <summary>
    /// A signature set named in a signature is a name like any other, so hover and definition find
    /// it in its module.
    /// </summary>
    [Fact]
    public async Task ASignatureSetInASignatureLeadsToItsDeclaration()
    {
        const string Sys = """
            .module sys
            .export .signature std = a8
            """;
        const string User = """
            .module main
            .use sys::std
            .segment CODE
            .proc main: std {
                rts
            }
            """;
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(Sys, User, timeout);

        var definition = await client.DefinitionAsync(MainUri, Locate.At(User, "main: s|td"), timeout);
        var hover = await client.HoverAsync(MainUri, Locate.At(User, "main: s|td"), timeout);

        Assert.NotNull(definition);
        Assert.Equal(GfxUri, definition.Uri);
        Assert.Equal(Locate.Span(Sys, ".signature |std"), definition.Range);
        Assert.NotNull(hover);
        Assert.Contains("```nt65\n.export .signature sys::std = a8\n```", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>Returns the next diagnostics published for one file, skipping those for other files.</summary>
    private static async Task<PublishDiagnosticsParams> NextForAsync(
        TestClient client, string uri, CancellationToken cancellation)
    {
        for (var i = 0; i < 8; i++)
        {
            var published = await client.NextDiagnosticsAsync(cancellation);
            if (published.Uri == uri)
                return published;
        }
        throw new InvalidOperationException($"nothing was published for {uri}");
    }

    private static Task<TestClient> OpenAsync(CancellationToken cancellation) => OpenAsync(Gfx, Main, cancellation);

    /// <summary>Opens <paramref name="gfx"/> as gfx.nt65 and <paramref name="main"/> as main.nt65.</summary>
    private static async Task<TestClient> OpenAsync(string gfx, string main, CancellationToken cancellation)
    {
        var client = await TestClient.StartAsync(cancellation);
        await client.OpenAsync(GfxUri, gfx);
        await client.NextDiagnosticsAsync(cancellation);
        await client.OpenAsync(MainUri, main);
        await NextForAsync(client, MainUri, cancellation);
        return client;
    }
}
