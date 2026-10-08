using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the <c>nt65/sources</c> request, which says where each value that the instruction at the
/// caret reads was set. Every range in the answer covers one whole line of the caret's document,
/// which is what the client highlights.
/// </summary>
public sealed class SourcesRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// On a call, the inputs are what the routine called reads. Each source is the line that set
    /// the value, and a value that reaches the call along two paths has two sources.
    /// </summary>
    [Fact]
    public async Task ACallsInputsComeFromTheLinesThatSetThem()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .proc putc {
                sta $10
                rts
            }
            .export .proc main {
                lda #1
                ldx $11
                beq @skip
                lda #2
            @skip:
                jsr pu|tc
                nop
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        Assert.Equal(new Range(new Position(6, 0), new Position(6, 20)), result.Routine);
        var input = Assert.Single(result.Inputs);
        Assert.Equal(("A", "register"), (input.Name, input.Category));
        Assert.Equal([7, 10], input.Sources.Select(source => source.Range.Start.Line));
        Assert.All(input.Sources, source => Assert.Equal(("instruction", "proven", 0), (source.Kind, source.Confidence, source.Range.Start.Character)));
        Assert.Equal(new Position(7, 10), input.Sources[0].Range.End);
        Assert.Empty(input.Through);
    }

    /// <summary>
    /// A call that keeps a register is a through line for it. Where a routine does not say what it
    /// keeps, the call is where the analysis lost track, and the source names it and says why.
    /// </summary>
    [Fact]
    public async Task ThroughLinesAndBlockersAreLines()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .proc CHROUT = $FFD2
            .proc keep: keeps x {
                lda #0
                rts
            }
            .export .proc main {
                ldx #1
                lda #2
                jsr keep
                stx $10
                jsr CHROUT
                sta $1|1
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        var input = Assert.Single(result.Inputs);
        var source = Assert.Single(input.Sources);
        Assert.Equal(("unknown", 12), (source.Kind, source.Range.Start.Line));
        Assert.Equal(12, source.Blocker?.Start.Line);
        Assert.Equal("`CHROUT` does not declare what it keeps", source.Reason);

        var (keeps, at) = Caret.In(text.Replace("    stx $10", "    stx $1|0", StringComparison.Ordinal));
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(null, keeps));
        await client.NextDiagnosticsAsync(Uri, timeout);
        var x = Assert.Single((await SourcesAsync(client, at, timeout))!.Inputs);
        Assert.Equal([8], x.Sources.Select(source => source.Range.Start.Line));
        Assert.Equal([10], x.Through.Select(line => line.Start.Line));
    }

    /// <summary>
    /// A location in memory that the routine called reads is an input too. Its sources are best
    /// guesses, and the bytes of one pointer share the pointer's group.
    /// </summary>
    [Fact]
    public async Task MemoryInputsAreBestGuessesGroupedBySymbol()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment ZEROPAGE
            .data ptr: .word
            .segment CODE
            .proc first {
                ldy #0
                lda (ptr),y
                rts
            }
            .export .proc main {
                lda #0
                sta ptr
                sta ptr+1
                jsr fir|st
                nop
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        var memory = result.Inputs.Where(input => input.Category == "memory").ToList();
        Assert.Equal(["ptr", "ptr+1"], memory.Select(input => input.Name));
        Assert.All(memory, input => Assert.Equal("ptr", input.Group));
        Assert.Equal([11, 12], memory.Select(input => Assert.Single(input.Sources).Range.Start.Line));
        Assert.All(memory, input => Assert.Equal("bestEffort", input.Sources[0].Confidence));
    }

    /// <summary>
    /// A line that might have changed a value in memory after its source set it is sent as a line
    /// of its own, apart from the sources, so a client can draw it as the doubt it is.
    /// </summary>
    [Fact]
    public async Task WhatMightAlsoChangeMemoryIsSentAsLines()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment ZEROPAGE
            .data count: .byte
            .data ptr: .word
            .segment CODE
            .export .proc main {
                sta count
                ldy #0
                sta (ptr),y
                lda co|unt
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        var count = Assert.Single(result.Inputs);
        Assert.Equal([6], count.Sources.Select(source => source.Range.Start.Line));
        Assert.Equal(new Range(new Position(8, 0), new Position(8, 15)), Assert.Single(count.Possibly));
    }

    /// <summary>A line that holds no instruction has no answer.</summary>
    [Fact]
    public async Task ALineWithNoInstructionHasNoAnswer()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc ma|in {
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));
        Assert.Null(await SourcesAsync(client, position, timeout));
    }

    private static Task<SourcesResult?> SourcesAsync(TestClient client, Position position, CancellationToken timeout) =>
        client.RequestAsync<SourcesResult?>("nt65/sources",
            new TextDocumentPositionParams(new TextDocumentIdentifier(Uri), position), timeout);
}
