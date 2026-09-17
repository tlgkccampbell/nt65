using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What an editor gets across modules: a name that crosses modules is one name, so definition,
/// references and rename cross with it, through the <c>.use</c> that brings it in, and an edit
/// in one module changes what is wrong with another.
/// </summary>
public sealed class WorkspaceRequestsTests
{
    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string MainUri = "file:///c:/work/main.nt65";

    private const string Gfx = """
        .module gfx
        .export clear, SCREEN

        SCREEN = $0400
        rows   = 25
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
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // `clear` on `jsr clear`, declared by gfx.nt65.
        var definition = await client.DefinitionAsync(MainUri, new Position(4, 8), timeout);

        Assert.NotNull(definition);
        Assert.Equal(GfxUri, definition.Uri);
        Assert.Equal(new Range(new Position(6, 6), new Position(6, 11)), definition.Range);
    }

    /// <summary>Hover on a name from another module says which module it came from.</summary>
    [Fact]
    public async Task HoverNamesTheModuleANameComesFrom()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(MainUri, new Position(4, 8), timeout);

        Assert.NotNull(hover);
        Assert.Contains("**routine** `gfx::clear`", hover.Contents.Value);
        Assert.Contains("from: `gfx.nt65`", hover.Contents.Value);
    }

    [Fact]
    public async Task ReferencesSpanEveryModuleThatNamesTheSymbol()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // From the declaration in gfx.nt65: the `.export`, the `.proc`, and in main the `.use`
        // and the call.
        var references = await client.ReferencesAsync(GfxUri, new Position(6, 6), true, timeout);

        Assert.Equal([GfxUri, GfxUri, MainUri, MainUri], references.Select(r => r.Uri));
    }

    /// <summary>A rename across modules rewrites the <c>.use</c> that brings the name in.</summary>
    [Fact]
    public async Task RenamingAnExportedNameEditsEveryModule()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var edit = await client.RenameAsync(MainUri, new Position(4, 8), "wipe", timeout);

        Assert.NotNull(edit);
        Assert.Equal([GfxUri, MainUri], edit.Changes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, edit.Changes[GfxUri].Count);
        Assert.Equal([1, 4], edit.Changes[MainUri].Select(change => change.Range.Start.Line));
        Assert.All(edit.Changes[MainUri], change => Assert.Equal("wipe", change.NewText));
    }

    /// <summary>
    /// A name a <c>.use ... as</c> gives is the using module's own: renaming the symbol leaves it
    /// alone, and renaming it renames only it.
    /// </summary>
    [Fact]
    public async Task ARenameKeepsAnAliasApartFromTheNameItStandsFor()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(GfxUri, Gfx);
        await client.NextDiagnosticsAsync(timeout);
        await client.OpenAsync(MainUri, ".module main\n.use gfx::clear as wipe\n.segment CODE\n.proc main {\n    jsr wipe\n    rts\n}\n");
        await NextForAsync(client, MainUri, timeout);

        var alias = await client.RenameAsync(MainUri, new Position(4, 8), "erase", timeout);
        Assert.NotNull(alias);
        Assert.Equal([MainUri], alias.Changes.Keys);
        Assert.Equal([1, 4], alias.Changes[MainUri].Select(change => change.Range.Start.Line));

        var symbol = await client.RenameAsync(GfxUri, new Position(6, 6), "blank", timeout);
        Assert.NotNull(symbol);
        Assert.Equal(2, symbol.Changes[GfxUri].Count);
        var inMain = Assert.Single(symbol.Changes[MainUri]);
        Assert.Equal(new Range(new Position(1, 10), new Position(1, 15)), inMain.Range);
    }

    /// <summary>A name another module keeps to itself is reported as private, not as missing.</summary>
    [Fact]
    public async Task NamingSomethingUnexportedIsReported()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(GfxUri, Gfx);
        await client.NextDiagnosticsAsync(timeout);
        await client.OpenAsync(MainUri, ".module main\nn = gfx::rows\n");

        var published = await NextForAsync(client, MainUri, timeout);
        Assert.Equal("`gfx::rows` is not exported by module `gfx`",
            Assert.Single(published.Diagnostics).Message);
    }

    /// <summary>
    /// An edit to one module republishes the others: dropping an export makes the module that
    /// used the name wrong, and the editor has to say so there.
    /// </summary>
    [Fact]
    public async Task AnEditInOneModuleChangesWhatIsWrongWithAnother()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // `.export clear, SCREEN` becomes `.export SCREEN`.
        await client.ChangeAsync(GfxUri, 2,
            new TextDocumentContentChangeEvent(new Range(new Position(1, 8), new Position(1, 15)), ""));

        var published = await NextForAsync(client, MainUri, timeout);
        Assert.Equal("`gfx::clear` is not exported by module `gfx`",
            Assert.Single(published.Diagnostics).Message);
    }

    /// <summary>
    /// An edit that leaves what other modules see of a module alone analyzes only that file, so
    /// main.nt65 is still the analysis from before the edit. What it names is what gfx.nt65
    /// declares now, wherever the edit moved it.
    /// </summary>
    [Fact]
    public async Task NamesStillCrossModulesAfterAnEditOnlyOneFileWasAnalyzedFor()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // A comment line above `.proc clear`, which moves it down a line.
        await client.ChangeAsync(GfxUri, 2,
            new TextDocumentContentChangeEvent(new Range(new Position(6, 0), new Position(6, 0)), "; wipes the screen\n"));
        await NextForAsync(client, MainUri, timeout);

        var definition = await client.DefinitionAsync(MainUri, new Position(4, 8), timeout);
        Assert.NotNull(definition);
        Assert.Equal(new Range(new Position(7, 6), new Position(7, 11)), definition.Range);

        var references = await client.ReferencesAsync(GfxUri, new Position(7, 6), true, timeout);
        Assert.Equal([GfxUri, GfxUri, MainUri, MainUri], references.Select(r => r.Uri));
    }

    /// <summary>A signature set named in a signature is a name like any other: hover and definition find it in its module.</summary>
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
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(Sys, User, timeout);

        var definition = await client.DefinitionAsync(MainUri, new Position(3, 13), timeout);
        var hover = await client.HoverAsync(MainUri, new Position(3, 13), timeout);

        Assert.NotNull(definition);
        Assert.Equal(GfxUri, definition.Uri);
        Assert.Equal(new Range(new Position(1, 19), new Position(1, 22)), definition.Range);
        Assert.NotNull(hover);
        Assert.Contains("**signature set** `sys::std`", hover.Contents.Value);
    }

    /// <summary>The next diagnostics published for one file, skipping the others.</summary>
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
