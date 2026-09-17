using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Semantic tokens: the names in a document, classified by what they refer to, which the client
/// draws over the TextMate grammar's colours.
/// </summary>
public sealed class SemanticTokensTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = """
        .module main
        .cpu 6502

        SCREEN = $0400
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

        .proc main {
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
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        var provider = client.Initialized.Capabilities.SemanticTokensProvider;
        Assert.NotNull(provider);
        Assert.True(provider.Full);
        Assert.Equal(NameHighlighting.Legend.TokenTypes, provider.Legend.TokenTypes);
    }

    /// <summary>
    /// Every name is classified by what it refers to, a member spelled like a register included,
    /// with its declarations marked and its constants read-only. Registers, mnemonics and
    /// directives are no names, and are left to the grammar.
    /// </summary>
    [Fact]
    public async Task EachNameIsClassifiedByWhatItRefersTo()
    {
        var timeout = TestContext.Current.CancellationToken;
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
    /// A change in one file can change what a name in another refers to, so a client that can be
    /// asked is told to fetch every document's tokens again.
    /// </summary>
    [Fact]
    public async Task AChangeAsksTheClientToFetchTokensAgain()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(null, null, timeout, refreshesTokens: true);
        await client.OpenAsync(Uri, Source);
        await client.NextTokensRefreshAsync(timeout);
    }

    /// <summary>Each token as its text, its type and its modifiers.</summary>
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
