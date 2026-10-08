using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the <c>nt65/directPages</c> request, which says how the program's routines share the
/// zero page and the direct pages. These tests check the shape the client draws from. The map's
/// own findings are checked in <see cref="Flow.DirectPageMapTests"/>.
/// </summary>
public sealed class DirectPagesRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// The routines that reach a location form call trees from the outermost caller down. Each
    /// node carries the calls that lead to it, its own accesses as whole lines, and its hazards.
    /// </summary>
    [Fact]
    public async Task ALocationsRoutinesAreCallTrees()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .segment ZEROPAGE
            .data tmp: .byte
            .segment CODE
            .export .proc main {
                lda #1
                sta tmp
                jsr inner
                lda tmp
                rts
            }
            .proc inner {
                sty tmp
                ldy tmp
                rts
            }
            """;
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Text));

        var result = await DirectPagesAsync(client, timeout);
        Assert.NotNull(result);
        Assert.Equal(("6502", true), (result.Cpu, result.Predicted));
        var page = Assert.Single(result.Pages);
        Assert.Equal(("$0000", 0L, "nested", true), (page.Id, page.Base!.Value, page.Relation, page.Hazard));
        var tmp = Assert.Single(page.Locations);
        Assert.Equal(("tmp", 0L, 1L, ".byte", 4), (tmp.Name, tmp.Offset!.Value, tmp.Size!.Value, tmp.Type, tmp.Accesses));
        Assert.Equal(new Position(2, 6), tmp.Declaration.Range.Start);

        var main = Assert.Single(tmp.Routines);
        Assert.Equal(("main", "temp"), (main.Name, main.Role));
        Assert.Empty(main.Via);
        Assert.Equal([6, 8], main.Accesses.Select(access => access.Place.Range.Start.Line));
        Assert.Equal((0, 11), (main.Accesses[0].Place.Range.Start.Character, main.Accesses[0].Place.Range.End.Character));
        Assert.Equal(["live across `jsr inner`", "`inner` sets it", "read again"], main.Hazards.Select(note => note.Text));
        Assert.Equal([7, 12, 8], main.Hazards.Select(note => note.Place!.Range.Start.Line));

        var inner = Assert.Single(main.Children);
        Assert.Equal(("inner", "temp"), (inner.Name, inner.Role));
        Assert.Equal(7, Assert.Single(inner.Via).Range.Start.Line);
    }

    /// <summary>
    /// On the 65816, each value of D is a page of its own. A page that overlaps another says so,
    /// and the locations that take the same bytes name each other. An interrupt handler that
    /// reaches memory after giving back the interrupted code's D is listed on the page whose D
    /// is not known, and under the location it names, marked as such.
    /// </summary>
    [Fact]
    public async Task EachDirectPageIsAPage()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .cpu 65816
            .segment HIGH: zp, dp = $0080
            .segment ZEROPAGE
            .data low: .byte[130]
            .data frames: .byte
            .segment HIGH
            .data high: .word
            .segment CODE
            .export .proc main: a8, dp = 0 {
                lda frames
                stz low
                jsr other
                lda low
                rts
            }
            .proc other: a8, dp = 0 {
                pea $0080
                pld
                stz high
                pea 0
                pld
                rts
            }
            .export .proc nmi: interrupt, native {
                sep #$20
                inc frames
                rti
            }
            """;
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Text));

        var result = await DirectPagesAsync(client, timeout);
        Assert.NotNull(result);
        Assert.Equal(["$0000", "$0080", "?"], result.Pages.Select(page => page.Id));
        var (zero, high, unknown) = (result.Pages[0], result.Pages[1], result.Pages[2]);
        Assert.Equal(["ZEROPAGE"], zero.Segments);
        Assert.Equal(["HIGH"], high.Segments);

        var overlap = Assert.Single(zero.Overlaps);
        Assert.Equal(("$0080", 0x80L, 0xffL), (overlap.Page, overlap.First, overlap.Last));
        var shared = Assert.Single(overlap.Shared);
        Assert.Equal(("low", "high", 0x80L, 0x81L), (shared.Here, shared.There, shared.First, shared.Last));

        var frames = zero.Locations.Single(location => location.Name == "frames");
        Assert.Equal("irq", frames.Relation);
        Assert.Equal(["main", "nmi"], frames.Routines.Select(routine => routine.Name));
        Assert.True(frames.Routines[0] is { Handler: false, Interrupt: false, Main: true });
        Assert.True(frames.Routines[1] is { Handler: true, Interrupt: true, Main: false, Unknown: true, Role: "inout" });

        var group = Assert.Single(unknown.Groups);
        Assert.Equal("interrupted", group.Reason);
        var nmi = Assert.Single(group.Routines);
        var use = Assert.Single(nmi.Uses);
        Assert.Equal(("frames", "$0000", "inout", true), (use.Name, use.Home, use.Role, use.Hazard));
    }

    /// <summary>
    /// How often a location is used counts each instruction as many times as the loops whose
    /// counts are known run it, and as many times as calls in such loops run its routine. An
    /// instruction in a loop whose count is not known is counted once and also counted apart.
    /// </summary>
    [Fact]
    public async Task CountedLoopsAndCallsMultiplyUses()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .segment ZEROPAGE
            .data n: .byte
            .segment CODE
            .export .proc main {
                ldx #4
            @loop:
                jsr bump
                dex
                bne @loop
            @wait:
                lda n
                beq @wait
                rts
            }
            .proc bump {
                ldy #3
            @inner:
                inc n
                dey
                bne @inner
                rts
            }
            """;
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Text));

        var result = await DirectPagesAsync(client, timeout);
        Assert.NotNull(result);
        var n = Assert.Single(Assert.Single(result.Pages).Locations);
        Assert.Equal((2, 13L, 1), (n.Accesses, n.PerPass, n.Uncounted));
        var main = Assert.Single(n.Routines);
        Assert.Equal((1L, true), (main.Accesses[0].Times, main.Accesses[0].Uncounted));
        var bump = Assert.Single(main.Children);
        Assert.Equal((4L, false), (bump.Runs, bump.RunsUncounted));
        Assert.Equal((3L, false), (bump.Accesses[0].Times, bump.Accesses[0].Uncounted));
    }

    private static Task<DirectPagesResult?> DirectPagesAsync(TestClient client, CancellationToken timeout) =>
        client.RequestAsync<DirectPagesResult?>("nt65/directPages", new DirectPagesParams(new TextDocumentIdentifier(Uri)), timeout);
}
