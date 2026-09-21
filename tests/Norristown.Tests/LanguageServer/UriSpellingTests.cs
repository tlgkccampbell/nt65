using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one this test means.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// One file is one file. VS Code escapes a drive's colon, <c>file:///c%3A/src</c>, and nt65
/// does not; a file named one way in a diagnostic and another in a go-to-definition is two
/// files to an editor, with the problems of one of them in a list nothing opens. So every
/// answer is spelled the way the client spelled the file, and this asks for every kind of
/// answer that names one.
/// </summary>
public sealed class UriSpellingTests
{
    private const string GfxUri = "file:///c%3A/work/gfx.nt65";

    private const string MainUri = "file:///c%3A/work/main.nt65";

    private const string Gfx = """
        .module gfx
        .export clear

        rows = 25
        .segment CODE
        .proc clear {
            rts
        }
        """;

    private const string Main = """
        .module main
        .use gfx::clear
        .export main
        .segment CODE
        .proc main {
            jsr clear
            rts
        }
        """;

    [Fact]
    public async Task EveryAnswerSpellsAFileTheWayTheClientDid()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(TestClient.Capable(), timeout);
        await client.OpenAsync(GfxUri, Gfx);
        await client.OpenAsync(MainUri, Main);

        // What is wrong with a file is published under the name the client gave it.
        Assert.Contains(GfxUri, (await client.NextDiagnosticsAsync(GfxUri, timeout)).Uri, StringComparison.Ordinal);

        // `clear` on `jsr clear`, declared in the other file.
        var at = new Position(5, 9);
        var definition = await client.DefinitionAsync(MainUri, at, timeout);
        Assert.Equal(GfxUri, definition!.Uri);

        var references = await client.ReferencesAsync(MainUri, at, includeDeclaration: true, timeout);
        Assert.Equal(
            [GfxUri, MainUri],
            references.Select(one => one.Uri).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        var renamed = await client.RenameAsync(MainUri, at, "wipe", timeout);
        Assert.Equal([GfxUri, MainUri], renamed!.Changes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            [GfxUri, MainUri], renamed.DocumentChanges!.Select(one => one.TextDocument.Uri).Order(StringComparer.Ordinal));

        // The fixes offered on `rows`, which nothing uses: each writes in the file it names.
        var fixes = await client.RequestAsync<IReadOnlyList<CodeAction>>("textDocument/codeAction",
            new CodeActionParams(
                new TextDocumentIdentifier(GfxUri),
                new Range(new Position(3, 0), new Position(3, 4)),
                new CodeActionContext([])),
            timeout);
        Assert.NotEmpty(fixes);
        Assert.All(fixes, action =>
        {
            Assert.All(action.Edit.Changes.Keys, uri => Assert.Equal(GfxUri, uri));
            if (action.Command?.Arguments is [string named, ..])
                Assert.Equal(GfxUri, named);
        });

        // The routine a call leads to, and what calls it: the item goes to the client and comes
        // back, so it has to name a file the way the client would.
        var prepared = Assert.Single(await client.RequestAsync<IReadOnlyList<CallHierarchyItem>>(
            "textDocument/prepareCallHierarchy", new { textDocument = new { uri = MainUri }, position = at }, timeout));
        Assert.Equal(GfxUri, prepared.Uri);
        var incoming = Assert.Single(await client.RequestAsync<IReadOnlyList<CallHierarchyIncomingCall>>(
            "callHierarchy/incomingCalls", new { item = prepared }, timeout));
        Assert.Equal(MainUri, incoming.From.Uri);

        var found = await client.RequestAsync<IReadOnlyList<SymbolInformation>>(
            "workspace/symbol", new WorkspaceSymbolParams("clear"), timeout);
        Assert.Equal(GfxUri, Assert.Single(found).Location.Uri);
    }
}
