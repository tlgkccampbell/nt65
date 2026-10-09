using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the <c>nt65/directPages</c> request, which says how the program's routines share the
/// zero page, the direct pages and the data in every other segment. These tests check the shape the client draws from. The map's
/// own findings are checked in <see cref="Flow.DataMapTests"/>.
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
        Assert.Equal("6502", result.Cpu);
        var page = Assert.Single(result.Pages);
        Assert.Equal(("$0000", 0L, "nested", true), (page.Id, page.Base!.Value, page.Relation, page.Hazard));
        Assert.Equal(["no config · layout guessed"], page.Notes.Select(note => note.Text));
        var tmp = Assert.Single(page.Locations);
        Assert.Equal(("tmp", 0L, 1L, ".byte", 4), (tmp.Name, tmp.Offset!.Value, tmp.Size!.Value, tmp.Type, tmp.Accesses));
        Assert.Equal("guessed", tmp.Layout);
        Assert.Equal(new Position(2, 6), tmp.Declaration?.Range.Start);

        var main = Assert.Single(tmp.Routines);
        Assert.Equal(("main", "temp"), (main.Name, main.Role));
        Assert.Empty(main.Via);
        Assert.Equal([6, 8], main.Accesses.Select(access => access.Place.Range.Start.Line));
        Assert.Equal((0, 11), (main.Accesses[0].Place.Range.Start.Character, main.Accesses[0].Place.Range.End.Character));
        Assert.Equal(["`jsr inner` runs between a write and a read of it", "`inner` uses it as a temporary", "read again here, after the call"], main.Hazards.Select(note => note.Text));
        Assert.Equal([7, 12, 8], main.Hazards.Select(note => note.Place!.Range.Start.Line));

        var inner = Assert.Single(main.Children);
        Assert.Equal(("inner", "temp"), (inner.Name, inner.Role));
        Assert.Equal(7, Assert.Single(inner.Via).Range.Start.Line);
    }

    /// <summary>
    /// A constant address that an instruction reaches through the zero page is a location named by
    /// its address. It has no declaration to go to.
    /// </summary>
    [Fact]
    public async Task AConstantAddressHasNoDeclaration()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .segment CODE
            .export .proc main {
                lda $FB
                rts
            }
            """;
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Text));

        var result = await DirectPagesAsync(client, timeout);
        Assert.NotNull(result);
        var location = Assert.Single(Assert.Single(result.Pages).Locations);
        Assert.Equal(("$00FB", 0xFBL, "fixed"), (location.Name, location.Address!.Value, location.Layout));
        Assert.Null(location.Declaration);
        Assert.Equal(("main", "in"), (Assert.Single(location.Routines).Name, location.Routines[0].Role));
    }

    /// <summary>
    /// On the 65816, each value of D is a page of its own. Without a linked config the layout is
    /// guessed, so pages that would overlap through it report no overlap. An interrupt handler that
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

        Assert.Empty(zero.Overlaps);
        Assert.Empty(high.Overlaps);
        Assert.All(zero.Locations, location => Assert.Equal("guessed", location.Layout));

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
        Assert.Equal(
            ["D is left as the interrupted code had it", "with D = $0000 it reaches $0082, `frames`", "with D = $0080 it reaches $0102, free"],
            use.Hazards.Select(note => note.Text));
        Assert.Equal([null, 26, 26], use.Hazards.Select(note => note.Place?.Range.Start.Line));
    }

    /// <summary>
    /// A hardware page carries every register declared in its range, each saying whether an
    /// instruction reaches it, so that the client can list only the reached ones in the tree and
    /// draw them all in the grid.
    /// </summary>
    [Fact]
    public async Task AHardwarePageSendsEveryDeclaredRegister()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .cpu 65816
            .mmio INIDISP: .byte = $2100
            .mmio OAMDATA: .byte = $2104
            .mmio NMITIMEN: .byte = $4200
            .segment CODE
            .export .proc main: a8, dp = 0 {
                pea $2100
                pld
                lda #$0F
                sta d:INIDISP
                pea 0
                pld
                rts
            }
            """;
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Text));

        var result = await DirectPagesAsync(client, timeout);
        Assert.NotNull(result);
        var page = Assert.Single(result.Pages);
        Assert.Equal(("$2100", true), (page.Id, page.Hardware));
        Assert.Equal(
            [("INIDISP", "hw", true, 1), ("OAMDATA", "hw", false, 0)],
            page.Locations.Select(location => (location.Name, location.Relation, location.Reached, location.Accesses)));
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

    /// <summary>
    /// A location whose address is taken carries the lines that take it, and two locations on one
    /// page that take the same byte each name the other with the kind of sharing.
    /// </summary>
    [Fact]
    public async Task ReferencesAndSharedBytesOnOnePage()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .data ptr: .addr = $FB
            .data low: .byte = $FB
            .segment ZEROPAGE
            .data tmp: .byte
            .segment CODE
            .export .proc main {
                sta low
                ldy #0
                lda (ptr),y
                ldx #tmp
                rts
            }
            """;
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Text));

        var result = await DirectPagesAsync(client, timeout);
        Assert.NotNull(result);
        var page = Assert.Single(result.Pages);
        var tmp = page.Locations.Single(location => location.Name == "tmp");
        Assert.Equal(("unused", 0, false), (tmp.Relation, tmp.Accesses, tmp.Reached));
        Assert.Equal([10], tmp.References.Select(place => place.Range.Start.Line));
        Assert.Equal((0, 12), (tmp.References[0].Range.Start.Character, tmp.References[0].Range.End.Character));

        var low = page.Locations.Single(location => location.Name == "low");
        var shared = Assert.Single(low.Shared);
        Assert.Equal(("low", "ptr", "$0000", 0xFBL, 0xFBL, "deliberate"), (shared.Here, shared.There, shared.Page, shared.First, shared.Last, shared.Kind));
        Assert.Empty(low.References);
    }

    /// <summary>
    /// Data off the pages comes as segments, after the pages. A location there has no offset, and
    /// no address without a build. A routine that takes a location's address comes with the lines
    /// that take it, and a table that takes it is only among the references.
    /// </summary>
    [Fact]
    public async Task DataOffThePagesComesAsSegments()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .segment BSS
            .data buffer: .byte[64]
            .segment CODE
            .export .proc fill {
                ldx #63
            @next:
                sta buffer,x
                dex
                bpl @next
                rts
            }
            .export .proc point {
                lda #<buffer
                ldx #>buffer
                rts
            }
            .segment RODATA
            .data table: .addr buffer
            """;
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Text));

        var result = await DirectPagesAsync(client, timeout);
        Assert.NotNull(result);
        Assert.Empty(result.Pages);
        Assert.Equal(["BSS", "RODATA"], result.Segments.Select(segment => segment.Id));
        var bss = result.Segments[0];
        Assert.Equal(("BSS", false, "own", false), (bss.Name, bss.Hardware, bss.Relation, bss.Hazard));
        var buffer = Assert.Single(bss.Locations);
        Assert.Equal(("buffer", null, null, 64L, "unknown"), (buffer.Name, buffer.Offset, buffer.Address, buffer.Size!.Value, buffer.Layout));
        Assert.Equal(("fill", "out"), (Assert.Single(buffer.Routines).Name, buffer.Routines[0].Role));
        Assert.Equal([13, 14, 18], buffer.References.Select(place => place.Range.Start.Line));
        var point = Assert.Single(buffer.Referrers);
        Assert.Equal(("point", 12), (point.Name, point.Declaration.Range.Start.Line));
        Assert.Equal([13, 14], point.Places.Select(place => place.Range.Start.Line));
    }

    private static Task<DirectPagesResult?> DirectPagesAsync(TestClient client, CancellationToken timeout) =>
        client.RequestAsync<DirectPagesResult?>("nt65/directPages", new DirectPagesParams(new TextDocumentIdentifier(Uri)), timeout);
}
