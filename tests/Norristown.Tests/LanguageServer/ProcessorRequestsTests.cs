using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the <c>nt65/processor</c> request, which gives the processor at the caret as rows for a
/// view that follows it.
/// </summary>
public sealed class ProcessorRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// On the 65816 the rows give the widths and the mode, D and B, each register with the constant
    /// it holds and the line that set it, the flags, and the stack keyed by stack-relative offset.
    /// </summary>
    [Fact]
    public async Task TheRowsGiveWhatTheHoverShowsBelowItsRule()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .cpu 65816

            .segment CODE
            .export .proc copy: a16, i16, native {
                pea $1234
                lda #8
                pha
                clc
                lda $1|0
                pla
                pla
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await ProcessorAsync(client, position, [], timeout);
        Assert.NotNull(result);
        Assert.Equal(("copy", 9), (result.Routine, result.Line));
        Assert.Equal(
            [
                ("state", "native, a16, i16", null),
                ("D", "as entered", null),
                ("B", "as entered", null),
                ("A", "$0008", "set at line 7"),
                ("X", "as entered", null),
                ("Y", "as entered", null),
                ("flags", "N 0  V ?  D ?  I ?  Z 0  C 0", "V as entered"),
                ("stack", "4 bytes pushed", "top first"),
            ],
            result.Rows.Select(row => (row.Key, row.Value, row.Detail)));
        Assert.Equal(6, result.Rows[3].Target?.Range.Start.Line);
        Assert.Equal(
            [("1,s", "$0008, 16-bit", "2 bytes"), ("3,s", "$1234", "2 bytes"), ("entry", "the stack the routine was entered with", null)],
            result.Rows[^1].Rows!.Select(row => (row.Key, row.Value, row.Detail)));
    }

    /// <summary>
    /// The 6502 has no widths, D or B, and no stack-relative operands, so its rows start at the
    /// registers and the top of the stack is keyed as the top.
    /// </summary>
    [Fact]
    public async Task OtherProcessorsHaveRegistersFlagsAndStackOnly()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc putc {
                pha
                txa
                pha
                ldx #3
                s|ec
                pla
                tax
                pla
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await ProcessorAsync(client, position, [], timeout);
        Assert.NotNull(result);
        Assert.Equal(["A", "X", "Y", "flags", "stack"], result.Rows.Select(row => row.Key));
        Assert.Equal(("$03", "set at line 7"), (result.Rows[1].Value, result.Rows[1].Detail));
        Assert.Equal("X as entered", result.Rows[0].Value);
        Assert.Equal(
            ["top", "", "entry"],
            result.Rows[^1].Rows!.Select(row => row.Key));
        Assert.Equal(["X as entered", "A as entered"], result.Rows[^1].Rows!.Take(2).Select(row => row.Value));
    }

    /// <summary>
    /// The line that opens a routine, and a blank line or a label, show the next statement that
    /// runs, so the opening line shows what the routine is entered with.
    /// </summary>
    [Fact]
    public async Task ALineWithNothingThatRunsShowsTheNextOne()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc ma|in {
                lda #1
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await ProcessorAsync(client, position, [], timeout);
        Assert.NotNull(result);
        Assert.Equal(3, result.Line);
        Assert.Equal(("A", "as entered"), (result.Rows[0].Key, result.Rows[0].Value));
        Assert.Equal("nothing pushed", result.Rows[^1].Value);
    }

    /// <summary>Outside every routine there is no processor to show.</summary>
    [Fact]
    public async Task OutsideARoutineThereIsNothing()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment C|ODE
            .export .proc main {
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        Assert.Null(await ProcessorAsync(client, position, [], timeout));
    }

    /// <summary>
    /// The result lists the calls to the routine. Chosen, one of them carries the stack on through
    /// the return address into what its caller had pushed.
    /// </summary>
    [Fact]
    public async Task AChosenCallerExtendsTheStack()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc main {
                lda #1
                pha
                jsr putc
                pla
                rts
            }

            .proc putc: reads a, keeps x, y {
                pha
                n|op
                pla
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var any = await ProcessorAsync(client, position, [], timeout);
        Assert.NotNull(any);
        var caller = Assert.Single(any.Callers);
        Assert.Equal(("main", 5), (caller.Name, caller.At.Range.Start.Line));
        Assert.Null(any.Caller);

        var chosen = await ProcessorAsync(client, position, [caller.At], timeout);
        Assert.NotNull(chosen);
        Assert.Equal(caller.At, chosen.Caller);
        var stack = chosen.Rows[^1];
        Assert.Equal("top first, as called from main", stack.Detail);
        Assert.Equal(
            [
                ("top", "A as entered", "1 byte"),
                ("", "return address", "2 bytes"),
                ("", "new", "1 byte, pushed by main"),
                ("entry", "the stack main was entered with", null),
            ],
            stack.Rows!.Select(row => (row.Key, row.Value, row.Detail)));
        Assert.Equal(5, stack.Rows![1].Target?.Range.Start.Line);
    }

    /// <summary>
    /// A line in a repetition or a macro body runs once per expansion. Where the expansions reach
    /// it in different states, the state row shows what they agree on, and the rows under it list
    /// each state with how many expansions it reaches, as the hover does. The first pass through
    /// the repetition is under <c>a8</c>, and its <c>rep #$20</c> makes A 16-bit for the second.
    /// </summary>
    [Fact]
    public async Task TheStateRowListsTheExpansionsOfAMacroBodyThatDiffer()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .cpu 65816

            .segment CODE
            .export .proc main: a8, i8, native {
                .repeat 2, i {
                    l|da $1234
                    rep #$20
                }
                sep #$20
                rts
            }
            """);
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, text));

        var result = await ProcessorAsync(client, position, [], timeout);
        Assert.NotNull(result);
        var state = result.Rows[0];
        Assert.Equal(("state", "native, a?, i8", "2 expansions differ"), (state.Key, state.Value, state.Detail));
        Assert.Equal(
            [("×1", "native, a16, i8"), ("×1", "native, a8, i8")],
            state.Rows!.Select(row => (row.Key, row.Value)));
    }

    private static Task<ProcessorResult?> ProcessorAsync(
        TestClient client, Position position, IReadOnlyList<Location> callers, CancellationToken timeout) =>
        client.RequestAsync<ProcessorResult?>(
            "nt65/processor", new ProcessorParams(new TextDocumentIdentifier(Uri), position, callers), timeout);
}
