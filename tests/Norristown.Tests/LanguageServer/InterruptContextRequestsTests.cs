using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Checks that the outline, the hover and the semantic tokens say which routines run under an
/// interrupt.
/// </summary>
public sealed class InterruptContextRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = """
        .module main
        .cpu 6502
        .segment CODE
        .export .proc main {
            jsr mix
            jsr load
            rts
        }
        .proc mix {
            rts
        }
        .proc play {
            jsr mix
            rts
        }
        .proc load {
            rts
        }
        .export .proc nmi: interrupt {
            jsr play
            rti
        }
        """;

    /// <summary>
    /// The outline names the handlers a routine runs under after its signature, and marks nothing
    /// that runs only in main. A handler's own signature already says <c>interrupt</c>.
    /// </summary>
    [Fact]
    public async Task TheOutlineSaysWhichHandlersARoutineRunsUnder()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Source));

        var symbols = await client.SymbolsAsync(Uri, timeout);

        Assert.Equal(
            ["main ", "mix under nmi and main", "play under nmi", "load ", "nmi : interrupt"],
            Flatten(symbols).Where(symbol => symbol.Kind == SymbolKind.Function).Select(symbol => $"{symbol.Name} {symbol.Detail}"));
    }

    /// <summary>
    /// The hover says which handlers a routine runs under, and says nothing of a routine that runs
    /// only in main.
    /// </summary>
    [Fact]
    public async Task TheHoverSaysWhereARoutineRunsFrom()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Source));

        Assert.Matches(@"context +under nmi and main", (await client.HoverAsync(Uri, Locate.At(Source, ".proc m|ix"), timeout))?.Contents.Value);
        Assert.DoesNotContain("context", (await client.HoverAsync(Uri, Locate.At(Source, ".proc l|oad"), timeout))?.Contents.Value, StringComparison.Ordinal);
        Assert.Matches(@"context +interrupt handler", (await client.HoverAsync(Uri, Locate.At(Source, ".proc n|mi"), timeout))?.Contents.Value);
    }

    /// <summary>
    /// A routine that only a vector table names and that returns with <c>rti</c> is walked as a
    /// handler without the mark. The hover and the outline say so, and the registers the hover
    /// says it preserves are those a marked handler with the same body preserves, since
    /// <c>rti</c> gives back the flags either way.
    /// </summary>
    [Fact]
    public async Task AnUnmarkedHandlerIsShownAsOne()
    {
        const string Vectored = """
            .module main
            .cpu 6502
            .segment ZEROPAGE
            .data count: .byte
            .segment CODE
            .export .proc main {
                rts
            }
            .export .data vectors: .addr[2] = nmi, irq
            .proc nmi: interrupt {
                inc count
                rti
            }
            .proc irq {
                inc count
                rti
            }
            """;
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Vectored));

        var marked = (await client.HoverAsync(Uri, Locate.At(Vectored, ".proc n|mi"), timeout))?.Contents.Value ?? "";
        var unmarked = (await client.HoverAsync(Uri, Locate.At(Vectored, ".proc i|rq"), timeout))?.Contents.Value ?? "";
        Assert.Matches(@"context +interrupt handler, by its `rti`; not marked `interrupt`", unmarked);
        var preserves = System.Text.RegularExpressions.Regex.Match(marked, @"preserves +([^\n|]*)");
        Assert.True(preserves.Success && preserves.Groups[1].Value.Trim().Length > 0, marked);
        Assert.Contains(preserves.Value, unmarked, StringComparison.Ordinal);
        var symbols = await client.SymbolsAsync(Uri, timeout);
        Assert.Contains("irq interrupt handler",
            Flatten(symbols).Where(symbol => symbol.Kind == SymbolKind.Function).Select(symbol => $"{symbol.Name} {symbol.Detail}"));
    }

    /// <summary>
    /// The names of routines that run under an interrupt carry the <c>interrupt</c> modifier,
    /// wherever they appear, so that a theme can color them.
    /// </summary>
    [Fact]
    public async Task TheNamesOfRoutinesUnderAnInterruptHaveTheirOwnModifier()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Source));

        var tokens = DecodedTokens.Describe(client.Initialized.Capabilities.SemanticTokensProvider!.Legend, Source,
            await client.SemanticTokensAsync(Uri, timeout));

        Assert.Equal(
            [
                "main function declaration",
                "mix function interrupt",
                "load function",
                "mix function declaration interrupt",
                "play function declaration interrupt",
                "mix function interrupt",
                "load function declaration",
                "nmi function declaration interrupt",
                "play function interrupt",
            ],
            tokens);
    }

    /// <summary>Returns the symbols of an outline and of everything nested in it, in order.</summary>
    private static IEnumerable<DocumentSymbol> Flatten(IEnumerable<DocumentSymbol> symbols) =>
        symbols.SelectMany(symbol => (IEnumerable<DocumentSymbol>)[symbol, .. Flatten(symbol.Children ?? [])]);
}
