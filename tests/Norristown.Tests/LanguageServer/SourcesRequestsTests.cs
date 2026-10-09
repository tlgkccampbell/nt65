using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the <c>nt65/sources</c> request, which says where each value that the instruction at the
/// caret reads was set, and where each value it writes is read. Every range in the answer covers one whole line of the caret's document,
/// which is what the client highlights.
/// </summary>
public sealed class SourcesRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A routine with two stores into the instruction at <c>@step</c>. The first store, on line 6,
    /// writes the opcode and lists what it writes. The second, on line 11, writes only the operand
    /// and lists nothing. The instruction is on line 15.
    /// </summary>
    private const string PatchedProgram = """
        .module main
        .segment ZEROPAGE
        .data count: .byte[1]
        .segment CODE
        .export .proc main {
            ldy #.opcode(ldy, imm)
            sty @step
            .patch @step as ldy
            ldx count
            bne @go
            ldy #5
            sty @step+1
            .patch @step
        @go:
        @step:
            ldx #1
            stx count
            rts
        }
        """;

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

    /// <summary>
    /// Each value the instruction writes is read by the instructions and calls that read it before
    /// it is written again. The return is a reader too, since the caller may read what comes back.
    /// </summary>
    [Fact]
    public async Task AnOutputIsReadByLinesCallsAndExits()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .proc put_char: reads y, keeps y {
                sty $10
                rts
            }
            .export .proc draw_name {
                ld|y #0
            @next:
                lda $20,y
                beq @done
                jsr put_char
                iny
                bne @next
            @done:
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        Assert.Empty(result.Inputs);
        var y = Assert.Single(result.Outputs);
        Assert.Equal(("Y", "register"), (y.Name, y.Category));
        Assert.Equal(
            [(9, "instruction"), (11, "call"), (12, "instruction"), (15, "exit")],
            y.Readers.Select(reader => (reader.Range.Start.Line, reader.Kind)));
        Assert.All(y.Readers, reader => Assert.Equal("proven", reader.Confidence));
    }

    /// <summary>A value written again before anything reads it has no readers, so it is not listed.</summary>
    [Fact]
    public async Task AnOutputNothingReadsIsLeftOut()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc main {
                ld|x #1
                ldx $11
                stx $10
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        Assert.Empty(result.Outputs);
    }

    /// <summary>
    /// A store to a named location is read by the lines that load it, and by the return, since
    /// memory outlives the routine. Both are best guesses.
    /// </summary>
    [Fact]
    public async Task AStoreIsReadByLaterLoadsAsABestGuess()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment ZEROPAGE
            .data count: .byte[1]
            .segment CODE
            .export .proc main {
                lda #3
                st|a count
                ldx count
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        var count = Assert.Single(result.Outputs, output => output.Category == "memory");
        Assert.Equal("count", count.Name);
        Assert.Equal(
            [(7, "instruction", "bestEffort"), (8, "exit", "bestEffort")],
            count.Readers.Select(reader => (reader.Range.Start.Line, reader.Kind, reader.Confidence)));
    }

    /// <summary>
    /// A reader of a store to memory carries what might have changed the value since the store, as
    /// a source does. The store through <c>ptr</c> on line 8 stands between the caret's store on
    /// line 6 and the load on line 9, and between it and the return on line 10, so both readers
    /// name it and it is sent as a line of its own.
    /// </summary>
    [Fact]
    public async Task AReaderOfMemoryNamesWhatMightHaveChangedIt()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment ZEROPAGE
            .data count: .byte
            .data ptr: .word
            .segment CODE
            .export .proc main {
                st|a count
                ldy #0
                sta (ptr),y
                lda count
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        var count = Assert.Single(result.Outputs, output => output.Category == "memory");
        Assert.Equal(
            [(9, "instruction", "or possibly `sta (ptr),y` on line 9"), (10, "exit", "or possibly `sta (ptr),y` on line 9")],
            count.Readers.Select(reader => (reader.Range.Start.Line, reader.Kind, reader.Reason)));
        Assert.Equal([8], count.Possibly.Select(range => range.Start.Line));
    }

    /// <summary>
    /// On a store with a <c>.patch</c>, the answer links the store to the instruction it writes
    /// into, with the instructions the <c>.patch</c> lists. Only the caret's store is linked.
    /// </summary>
    [Fact]
    public async Task AStoreIsLinkedToTheInstructionItPatches()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In(CaretAt(PatchedProgram.IndexOf("sty @step", StringComparison.Ordinal) + 2));
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        var link = Assert.Single(result.Patches);
        Assert.Equal((6, 15, "@step"), (link.Store.Start.Line, link.Target.Start.Line, link.Name));
        Assert.Equal(["ldy"], link.Variants);
    }

    /// <summary>
    /// On a <c>.patch</c> line, which is no instruction, the answer still links the store above
    /// it, and opens the routine the store is in.
    /// </summary>
    [Fact]
    public async Task APatchLineIsLinkedLikeItsStore()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In(CaretAt(PatchedProgram.LastIndexOf(".patch", StringComparison.Ordinal) + 4));
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        Assert.Equal(4, result.Routine.Start.Line);
        Assert.Empty(result.Inputs);
        var link = Assert.Single(result.Patches);
        Assert.Equal((11, 15), (link.Store.Start.Line, link.Target.Start.Line));
        Assert.Empty(link.Variants);
    }

    /// <summary>On a patched instruction, the answer links every store that writes into it, in order.</summary>
    [Fact]
    public async Task APatchedInstructionIsLinkedToEachStore()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In(CaretAt(PatchedProgram.LastIndexOf("ldx #1", StringComparison.Ordinal) + 2));
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        Assert.Equal([(6, 15), (11, 15)], result.Patches.Select(link => (link.Store.Start.Line, link.Target.Start.Line)));
    }

    /// <summary>An instruction that takes no part in a store into code has no links.</summary>
    [Fact]
    public async Task AnUnpatchedInstructionHasNoLinks()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In(CaretAt(PatchedProgram.IndexOf("stx count", StringComparison.Ordinal) + 2));
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await SourcesAsync(client, position, timeout);
        Assert.NotNull(result);
        Assert.Empty(result.Patches);
    }

    /// <summary>Returns <see cref="PatchedProgram"/> with the caret marked at <paramref name="offset"/>.</summary>
    private static string CaretAt(int offset) => PatchedProgram.Insert(offset, "|");

    private static Task<SourcesResult?> SourcesAsync(TestClient client, Position position, CancellationToken timeout) =>
        client.RequestAsync<SourcesResult?>("nt65/sources",
            new TextDocumentPositionParams(new TextDocumentIdentifier(Uri), position), timeout);
}
