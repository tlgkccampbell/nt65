using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests semantic tokens, which classify the names in a document by what they refer to. The
/// client draws them over the TextMate grammar's colours.
/// </summary>
public sealed class SemanticTokensTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = """
        .module main
        .cpu 6502

        .const SCREEN = $0400
        .func twice(n) = n * 2

        .enum Joy {
            A = $80
            X = $40
        }

        .struct Point {
            x: .byte
            y: .byte
        }

        .macro poke(value: operand) {
            lda value
            sta SCREEN
        }

        .segment CODE
        .data origin: .type Point { x = 1, y = 2 }

        .export .proc main {
        @loop:
            lda #Joy::A
            ldx #Point::y
            lda origin,x
            lda #twice(2)
            poke!({#Joy::X})
            bne @loop
            rts
        }
        """;

    [Fact]
    public async Task AnnouncesTheLegend()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);

        var provider = client.Initialized.Capabilities.SemanticTokensProvider;
        Assert.NotNull(provider);
        Assert.True(provider.Full.Delta);
        Assert.True(provider.Range);
        Assert.Equal(NameHighlighting.Legend.TokenTypes, provider.Legend.TokenTypes);
    }

    /// <summary>
    /// Every name is classified by what it refers to, including a member spelled like a register,
    /// with its declarations marked and its constants read-only. Registers, mnemonics and
    /// directives are not names, and are left to the grammar to colour.
    /// </summary>
    [Fact]
    public async Task EachNameIsClassifiedByWhatItRefersTo()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        var tokens = Decode(client.Initialized.Capabilities.SemanticTokensProvider!.Legend, Source,
            await client.SemanticTokensAsync(Uri, timeout));

        Assert.Equal(
            [
                "SCREEN variable declaration readonly",
                "twice function declaration",
                "n parameter declaration",
                "n parameter",
                "Joy enum declaration",
                "A enumMember declaration readonly",
                "X enumMember declaration readonly",
                "Point struct declaration",
                "x property declaration",
                "y property declaration",
                "poke macro declaration",
                "value parameter declaration",
                "value parameter",
                "SCREEN variable readonly",
                "origin variable declaration",
                "Point struct",
                "x property",
                "y property",
                "main function declaration",
                "@loop label declaration",
                "Joy enum",
                "A enumMember readonly",
                "Point struct",
                "y property",
                "origin variable",
                "twice function",
                "poke macro",
                "Joy enum",
                "X enumMember readonly",
                "@loop label",
            ],
            tokens);
    }

    /// <summary>
    /// A change in one file can change what a name in another refers to, so a client that
    /// supports refresh requests is asked to refetch every document's tokens.
    /// </summary>
    [Fact]
    public async Task AChangeAsksTheClientToFetchTokensAgain()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(null, null, timeout, refreshesTokens: true);
        await client.OpenAsync(Uri, Source);
        await client.NextTokensRefreshAsync(timeout);
    }

    /// <summary>
    /// For a long file, the client can ask for the tokens of just the lines it is showing, and
    /// can ask for only what changed since its last full answer instead of every number again.
    /// </summary>
    [Fact]
    public async Task ALongFileIsAskedAboutAScreenfulAndAChangeAtATime()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        var legend = client.Initialized.Capabilities.SemanticTokensProvider!.Legend;

        // The `.enum` block and nothing above or below it.
        var part = await client.SemanticTokensRangeAsync(Uri, 6, 9, timeout);
        Assert.Equal(
            ["Joy enum declaration", "A enumMember declaration readonly", "X enumMember declaration readonly"],
            Decode(legend, Source, part));
        Assert.Null(part.ResultId);

        // A full answer carries a result id, and the delta against it is one edit to the numbers
        // rather than all of them again.
        var whole = await client.SemanticTokensAsync(Uri, timeout);
        Assert.NotNull(whole.ResultId);
        var routine = Locate.At(Source, ".export .proc main");
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(new Range(routine, routine), "\n"));
        await client.NextDiagnosticsAsync(timeout);

        // A line added above the routine changes one number, which is how many lines the
        // routine's first name sits below the name before it. The other hundred and forty-nine
        // numbers are not sent again.
        var changed = await client.SemanticTokensDeltaAsync(Uri, whole.ResultId!, timeout);
        var edit = Assert.Single(changed.Edits);
        Assert.Equal(1, edit.DeleteCount);
        Assert.Single(edit.Data!);

        // A client holding an answer this server no longer has is given the whole thing.
        var again = await client.RequestAsync<SemanticTokens>("textDocument/semanticTokens/full/delta",
            new { textDocument = new { uri = Uri }, previousResultId = "gone" }, timeout);
        Assert.NotEmpty(again.Data);
    }

    /// <summary>Returns each token as its text, its type and its modifiers.</summary>
    private static List<string> Decode(SemanticTokensLegend legend, string source, SemanticTokens tokens)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var decoded = new List<string>();
        var (line, character) = (0, 0);
        for (var i = 0; i < tokens.Data.Count; i += 5)
        {
            line += tokens.Data[i];
            character = tokens.Data[i] == 0 ? character + tokens.Data[i + 1] : tokens.Data[i + 1];
            var modifiers = Enumerable.Range(0, legend.TokenModifiers.Count)
                .Where(bit => (tokens.Data[i + 4] & (1 << bit)) != 0)
                .Select(bit => legend.TokenModifiers[bit]);
            decoded.Add(string.Join(' ',
                [lines[line].Substring(character, tokens.Data[i + 2]), legend.TokenTypes[tokens.Data[i + 3]], .. modifiers]));
        }
        return decoded;
    }
}
