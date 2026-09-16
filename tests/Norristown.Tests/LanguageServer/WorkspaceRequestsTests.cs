using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What Stage 6 gives an editor: a name that crosses files is one name, so definition,
/// references and rename cross with it, and an edit in one file changes what is wrong with
/// another.
/// </summary>
public sealed class WorkspaceRequestsTests
{
    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string MainUri = "file:///c:/work/main.nt65";

    private const string Gfx = """
        .export clear, SCREEN

        SCREEN = $0400
        rows   = 25

        .proc clear {
            rts
        }
        """;

    private const string Main = """
        .proc main {
            jsr clear
            lda #<SCREEN
            rts
        }
        """;

    [Fact]
    public async Task DefinitionCrossesIntoTheFileThatDeclaresTheName()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // `clear` on `jsr clear`, declared by gfx.nt65.
        var definition = await client.DefinitionAsync(MainUri, new Position(1, 8), timeout);

        Assert.NotNull(definition);
        Assert.Equal(GfxUri, definition.Uri);
        Assert.Equal(new Range(new Position(5, 6), new Position(5, 11)), definition.Range);
    }

    /// <summary>Hover on a name from another module says which module it came from.</summary>
    [Fact]
    public async Task HoverNamesTheModuleANameComesFrom()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(MainUri, new Position(1, 8), timeout);

        Assert.NotNull(hover);
        Assert.Contains("**routine** `clear`", hover.Contents.Value);
        Assert.Contains("from: `gfx.nt65`", hover.Contents.Value);
    }

    [Fact]
    public async Task ReferencesSpanEveryFileThatNamesTheSymbol()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // From the declaration in gfx.nt65: the `.export`, the `.proc` and the call in main.
        var references = await client.ReferencesAsync(GfxUri, new Position(5, 6), true, timeout);

        Assert.Equal([GfxUri, GfxUri, MainUri], references.Select(r => r.Uri));
    }

    [Fact]
    public async Task RenamingAnExportedNameEditsEveryFile()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var edit = await client.RenameAsync(MainUri, new Position(1, 8), "wipe", timeout);

        Assert.NotNull(edit);
        Assert.Equal([GfxUri, MainUri], edit.Changes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, edit.Changes[GfxUri].Count);
        Assert.Equal("wipe", Assert.Single(edit.Changes[MainUri]).NewText);
    }

    /// <summary>A name another file keeps to itself is reported as private, not as missing.</summary>
    [Fact]
    public async Task NamingSomethingUnexportedIsReported()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(GfxUri, Gfx);
        await client.NextDiagnosticsAsync(timeout);
        await client.OpenAsync(MainUri, "n = rows\n");

        var published = await NextForAsync(client, MainUri, timeout);
        Assert.Equal("`rows` is declared in `gfx.nt65` and is not exported",
            Assert.Single(published.Diagnostics).Message);
    }

    /// <summary>
    /// An edit to one file republishes the others: dropping an export makes the file that
    /// used the name wrong, and the editor has to say so there.
    /// </summary>
    [Fact]
    public async Task AnEditInOneFileChangesWhatIsWrongWithAnother()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // `.export clear, SCREEN` becomes `.export SCREEN`.
        await client.ChangeAsync(GfxUri, 2,
            new TextDocumentContentChangeEvent(new Range(new Position(0, 8), new Position(0, 15)), ""));

        var published = await NextForAsync(client, MainUri, timeout);
        Assert.Equal("`clear` is declared in `gfx.nt65` and is not exported",
            Assert.Single(published.Diagnostics).Message);
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

    private static async Task<TestClient> OpenAsync(CancellationToken cancellation)
    {
        var client = await TestClient.StartAsync(cancellation);
        await client.OpenAsync(GfxUri, Gfx);
        await client.NextDiagnosticsAsync(cancellation);
        await client.OpenAsync(MainUri, Main);
        await NextForAsync(client, MainUri, cancellation);
        return client;
    }
}
