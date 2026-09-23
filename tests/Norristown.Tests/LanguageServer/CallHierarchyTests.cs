using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Which routines call a routine, and which routines it calls, across modules. The call edges
/// come from the flow analysis: a <c>jsr</c> is one, and so is a tail jump, which passes control
/// to a routine that then returns straight to the jumping routine's caller.
/// </summary>
public sealed class CallHierarchyTests
{
    private const string GfxUri = "file:///c:/work/gfx.nt65";
    private const string MainUri = "file:///c:/work/main.nt65";

    private const string Gfx = """
        .module gfx
        .export clear
        .segment CODE
        .proc clear {
            jsr fill
            rts
        }

        .proc fill {
            rts
        }
        """;

    private const string Main = """
        .module main
        .export main, again
        .segment CODE
        .proc main {
            jsr gfx::clear
            jsr gfx::clear
            rts
        }

        .proc again {
            jmp gfx::clear
        }
        """;

    /// <summary>With the caret on a routine's name, the hierarchy starts at the routine's declaration, even in another file.</summary>
    [Fact]
    public async Task APathToARoutineStartsAHierarchyAtWhereItIsDeclared()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // `jsr gfx::clear` in main.nt65, on the `clear`.
        var items = await PrepareAsync(client, MainUri, Locate.At(Main, "jsr gfx::|clear"), timeout);

        var item = Assert.Single(items);
        Assert.Equal("clear", item.Name);
        Assert.Equal(SymbolKind.Function, item.Kind);
        Assert.Equal(GfxUri, item.Uri);
        Assert.Equal("gfx::clear", item.Detail);
        Assert.Equal(Locate.At(Gfx, ".proc |clear"), item.SelectionRange.Start);

        // The whole declaration is what the client reveals, not just the name.
        Assert.Equal(3, item.Range.Start.Line);
        Assert.Equal(6, item.Range.End.Line);
    }

    /// <summary>A name that declares no routine starts no hierarchy.</summary>
    [Fact]
    public async Task ANameThatIsNoRoutineStartsNothing()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // The `.module` name on line 1 of main.nt65.
        Assert.Empty(await PrepareAsync(client, MainUri, Locate.At(Main, ".module m|ain"), timeout));
    }

    /// <summary>
    /// Everything that calls a routine, from every module: each caller is listed once, with every
    /// call it makes. A tail jump counts as a call, because the routine jumped to returns to the
    /// jumping routine's caller.
    /// </summary>
    [Fact]
    public async Task IncomingCallsAreEveryCallerAcrossTheProgram()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);
        var item = Assert.Single(await PrepareAsync(client, MainUri, Locate.At(Main, "jsr gfx::|clear"), timeout));

        var callers = await client.RequestAsync<IReadOnlyList<CallHierarchyIncomingCall>>(
            "callHierarchy/incomingCalls", new { item }, timeout);

        Assert.Equal(["main", "again"], callers.Select(call => call.From.Name));
        Assert.All(callers, call => Assert.Equal(MainUri, call.From.Uri));
        Assert.Equal([4, 5], callers[0].FromRanges.Select(range => range.Start.Line));
        Assert.Equal([10], callers[1].FromRanges.Select(range => range.Start.Line));
    }

    /// <summary>What a routine calls: the same call edges, followed in the other direction.</summary>
    [Fact]
    public async Task OutgoingCallsAreWhatTheRoutineItselfCalls()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);
        var clear = Assert.Single(await PrepareAsync(client, MainUri, Locate.At(Main, "jsr gfx::|clear"), timeout));
        var main = Assert.Single(await PrepareAsync(client, MainUri, Locate.At(Main, ".proc |main"), timeout));

        var inClear = await client.RequestAsync<IReadOnlyList<CallHierarchyOutgoingCall>>(
            "callHierarchy/outgoingCalls", new { item = clear }, timeout);
        var inMain = await client.RequestAsync<IReadOnlyList<CallHierarchyOutgoingCall>>(
            "callHierarchy/outgoingCalls", new { item = main }, timeout);

        var fill = Assert.Single(inClear);
        Assert.Equal("fill", fill.To.Name);
        Assert.Equal(GfxUri, fill.To.Uri);
        Assert.Equal([4], fill.FromRanges.Select(range => range.Start.Line));

        var called = Assert.Single(inMain);
        Assert.Equal("clear", called.To.Name);
        Assert.Equal([4, 5], called.FromRanges.Select(range => range.Start.Line));
    }

    private static Task<IReadOnlyList<CallHierarchyItem>> PrepareAsync(
        TestClient client, string uri, Position position, CancellationToken timeout) =>
        client.RequestAsync<IReadOnlyList<CallHierarchyItem>>("textDocument/prepareCallHierarchy",
            new { textDocument = new { uri }, position }, timeout);

    private static Task<TestClient> OpenAsync(CancellationToken timeout) =>
        TestClient.OpenedAsync(timeout, (GfxUri, Gfx.ReplaceLineEndings("\n")), (MainUri, Main.ReplaceLineEndings("\n")));
}
