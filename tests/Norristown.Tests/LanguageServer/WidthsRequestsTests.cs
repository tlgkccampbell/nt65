using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the <c>nt65/widths</c> request, which lists the runs of lines on which the 65816's
/// register widths and mode are the same.
/// </summary>
public sealed class WidthsRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A line's widths are the ones its instruction runs with, so a <c>rep</c> or a <c>sep</c> is
    /// drawn with the widths before it. The line that opens the routine has the widths it is
    /// entered with, and the line that closes it has none.
    /// </summary>
    [Fact]
    public async Task EachLineHasTheWidthsItsInstructionRunsWith()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, """
            .module main
            .cpu 65816

            .segment CODE
            .export .proc update: a8, i16, native {
                rep #$20
                lda #$1234
                sta $10
                sep #$20
                lda #$12
                sep #$10
                ldx #$01
                rep #$10
                rts
            }
            """));

        Assert.Equal(
            [(4, 5, 8, 16), (6, 8, 16, 16), (9, 10, 8, 16), (11, 12, 8, 8), (13, 13, 8, 16)],
            Runs(await WidthsAsync(client, timeout)));
    }

    /// <summary>
    /// Emulation mode is a run of its own, without widths. Leaving it with <c>xce</c> gives the
    /// 8-bit widths that emulation pinned.
    /// </summary>
    [Fact]
    public async Task EmulationIsDrawnAsTheMode()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, """
            .module main
            .cpu 65816

            .segment CODE
            .export .proc reset: emu, noreturn {
                sei
                clc
                xce
                rep #$30
            @forever:
                bra @forever
            }
            """));

        var runs = (await WidthsAsync(client, timeout))!.Runs;
        Assert.Equal(
            [(4, 7, null, null, true), (8, 8, 8, 8, false), (9, 10, 16, 16, false)],
            runs.Select(run => (run.First, run.Last, run.A, run.Index, run.Emulation)));
    }

    /// <summary>
    /// A comment or a blank line has the widths of the next line, which is what the code below it
    /// runs with. A label has the widths its block is entered with.
    /// </summary>
    [Fact]
    public async Task LinesWithoutStepsTakeTheWidthsOfTheNextLine()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, """
            .module main
            .cpu 65816

            .segment CODE
            .export .proc pick: a8, i8, native {
                rep #$20
                ; A is 16 bits from here
                bcc @wide

                sep #$20
                rts
            @wide:
                sep #$20
                rts
            }
            """));

        Assert.Equal(
            [(4, 5, 8, 8), (6, 9, 16, 8), (10, 10, 8, 8), (11, 12, 16, 8), (13, 13, 8, 8)],
            Runs(await WidthsAsync(client, timeout)));
    }

    /// <summary>
    /// A macro's call is one line for its whole body, and shows the widths the body is entered
    /// with. The lines of the macro's own declaration have none.
    /// </summary>
    [Fact]
    public async Task AMacroCallHasTheWidthsItsBodyIsEnteredWith()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, """
            .module main
            .cpu 65816

            .macro wide() {
                rep #$20
                lda #$1234
            }

            .segment CODE
            .export .proc main: a8, i8, native -> a16 {
                wide!()
                sta $10
                rts
            }
            """));

        Assert.Equal([(9, 10, 8, 8), (11, 12, 16, 8)], Runs(await WidthsAsync(client, timeout)));
    }

    /// <summary>
    /// A line that nothing reaches has no widths, and neither has the blank line before it, which
    /// takes the widths of the line after.
    /// </summary>
    [Fact]
    public async Task DeadCodeHasNoWidths()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, """
            .module main
            .cpu 65816

            .segment CODE
            .export .proc spin: a8, i8 {
            @forever:
                bra @forever

                lda #1
                rts
            }
            """));

        Assert.Equal([(4, 6, 8, 8)], Runs(await WidthsAsync(client, timeout)));
    }

    /// <summary>A file for a processor with no widths has nothing to draw.</summary>
    [Fact]
    public async Task OtherProcessorsHaveNoWidths()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, """
            .module main
            .segment CODE
            .export .proc main {
                lda #1
                rts
            }
            """));

        Assert.Null(await WidthsAsync(client, timeout));
    }

    private static IEnumerable<(int First, int Last, int? A, int? Index)> Runs(WidthsResult? result) =>
        result!.Runs.Select(run => (run.First, run.Last, run.A, run.Index));

    private static Task<WidthsResult?> WidthsAsync(TestClient client, CancellationToken timeout) =>
        client.RequestAsync<WidthsResult?>("nt65/widths", new WidthsParams(new TextDocumentIdentifier(Uri)), timeout);
}
