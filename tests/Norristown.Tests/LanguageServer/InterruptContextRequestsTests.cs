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
    /// The names of routines that run under an interrupt carry the <c>interrupt</c> modifier,
    /// wherever they appear, so that a theme can color them.
    /// </summary>
    [Fact]
    public async Task TheNamesOfRoutinesUnderAnInterruptHaveTheirOwnModifier()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Source));

        var tokens = SemanticTokensTests.Decode(client.Initialized.Capabilities.SemanticTokensProvider!.Legend, Source,
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
