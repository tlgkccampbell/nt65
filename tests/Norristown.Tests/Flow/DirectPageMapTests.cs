using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks the map of how a program's routines share the zero page and the direct pages. The map
/// is only for showing, so these tests check what an editor draws: the pages, the locations on
/// each, which routines use them and how, and the hazards.
/// </summary>
public sealed class DirectPageMapTests
{
    /// <summary>The sample program of the design, on the 65816, with three direct pages and the PPU's registers.</summary>
    private const string Snes = """
        .mmio INIDISP: .byte = $2100
        .mmio OAMDATA: .byte = $2104
        .mmio TM:      .byte = $212C
        .mmio NMITIMEN: .byte = $4200
        .segment ZEROPAGE
        .data ptr:    .addr
        .data tmp:    .word
        .data frames: .byte
        .data hdma:   .byte[128]
        .segment M7ZP: zp, dp = $0080
        .segment M7ZP
        .data matrix: .word[4]
        .segment NMIZP: zp, dp = $0100
        .segment NMIZP
        .data scroll: .word
        .segment BSS
        .data map:    .word[1024]
        .segment CODE
        .proc main: a8, i16, dp = 0, noreturn {
            jsr clear_map
            jsr copy_title
            rep #$20
            jsr m7_identity
            sep #$20
            jsr screen_on
        @wait:
            wai
            lda frames
            cmp #120
            bcc @wait
        @idle:
            wai
            bra @idle
        }
        .proc clear_map: a8, i16, dp = 0 {
            ldx #map
            stx ptr
            ldy #0
            lda #0
        @fill:
            sta (ptr),y
            iny
            cpy #2048
            bne @fill
            rts
        }
        .proc copy_title: a8, i16, dp = 0 {
            ldx #map
            stx ptr
            ldx #0
            stx tmp
        @next:
            ldx tmp
            lda map
            beq @done
            jsr plot
            ldx tmp
            inx
            stx tmp
            bra @next
        @done:
            rts
        }
        .proc plot: a8, i16, dp = 0 {
            sty tmp
            ldy #0
            sta (ptr),y
            ldy ptr
            iny
            iny
            sty ptr
            ldy tmp
            rts
        }
        .proc m7_identity: a16, i16, dp = 0 {
            phd
            pea $0080
            pld
            lda #$0100
            sta matrix
            sta matrix + 6
            stz matrix + 2
            stz matrix + 4
            pld
            rts
        }
        .proc screen_on: a8, i16, dp = 0 {
            lda #$80
            sta NMITIMEN
            pea $2100
            pld
            lda #1
            sta d:TM
            lda #$0F
            sta d:INIDISP
            pea 0
            pld
            rts
        }
        .proc nmi: interrupt, native {
            rep #$30
            pha
            phd
            pea $0100
            pld
            inc scroll
            pld
            sep #$20
            inc frames
            rep #$30
            pla
            rti
        }
        """;

    /// <summary>
    /// The design's sample has a page for each value of D it sets, and the hardware registers it
    /// reaches with D at $2100. It shows each kind of sharing: a pointer passed between routines,
    /// a temporary relied on across a call to a routine that uses it too, a counter that an
    /// interrupt handler bumps, and an array that takes the bytes of another page's matrix.
    /// </summary>
    [Fact]
    public void TheSampleShowsEachKindOfSharing()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Nested hazard=True used=133 direct=13",
                "  ⧉ $0080 $0080-$00ff hdma/matrix",
                "  ptr +0 x2 .addr Shared",
                "    clear_map Temp 2",
                "    copy_title Out 1",
                "    plot InOut 3",
                "  tmp +2 x2 .word Nested",
                "    copy_title Temp 4",
                "      ⚠ live across `jsr plot` @ jsr plot",
                "      ⚠ `plot` sets it @ sty tmp",
                "      ◦ read again @ ldx tmp",
                "    plot Temp 2",
                "  frames +4 x1 .byte Interrupt",
                "    main In 1",
                "    nmi InOut 1 handler unknown",
                "      ⚠ D is the interrupted code's @ ",
                "  hdma +5 x128 .byte[128] Unused",
                "page $0080 [M7ZP] Own hazard=False used=8 direct=4",
                "  ⧉ $0000 $0080-$00ff matrix/hdma",
                "  ⧉ $0100 $0100-$017f ",
                "  matrix +0 x8 .word[4] Own",
                "    m7_identity Out 4",
                "page $0100 [NMIZP] Own hazard=False used=2 direct=1",
                "  ⧉ $0080 $0100-$017f ",
                "  scroll +0 x2 .word Own",
                "    nmi InOut 1 handler",
                "page $2100 [] Hardware hazard=False used=2 direct=2",
                "  INIDISP +0 x1 .byte Hardware fixed",
                "    screen_on Write 1",
                "  TM +44 x1 .byte Hardware fixed",
                "    screen_on Write 1",
                "page ? [] Unused hazard=True used=0 direct=0",
                "  ? nmi frames Interrupted InOut",
            ],
            Render("65816", Snes));
    }

    /// <summary>
    /// On the 6502 the zero page is the only page. An address alias below $100 lives there at the
    /// address it gives, and data in the zero-page segment is laid out after the data before it.
    /// </summary>
    [Fact]
    public void OnThe6502TheZeroPageIsTheOnlyPage()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Own hazard=False used=4 direct=2",
                "  count +0 x1 .byte Own",
                "    main Out 1",
                "  total +1 x1 .byte Unused",
                "  ptr +251 x2 .addr Own fixed",
                "    main In 1",
            ],
            Render("6502", """
                .data ptr: .addr = $FB
                .segment ZEROPAGE
                .data count: .byte
                .data total: .byte
                .segment CODE
                .export .proc main {
                    ldy #0
                    lda (ptr),y
                    sta count
                    rts
                }
                """));
    }

    /// <summary>
    /// A routine that writes a location and then calls a routine that reads it first passes it a
    /// value, which is no hazard. Only a call to a routine that writes it without reading it
    /// first, followed by a read, is one.
    /// </summary>
    [Fact]
    public void PassingAValueToACallIsNoHazard()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Shared hazard=False used=1 direct=3",
                "  arg +0 x1 .byte Shared",
                "    main Temp 2",
                "    step InOut 1",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data arg: .byte
                .segment CODE
                .export .proc main {
                    lda #1
                    sta arg
                    jsr step
                    lda arg
                    rts
                }
                .proc step {
                    inc arg
                    rts
                }
                """));
    }

    /// <summary>Returns the map of <paramref name="text"/> as lines of text, one for each page, location, use and note.</summary>
    private static List<string> Render(string cpu, string text)
    {
        var analysis = FlowFragment.Analyze(cpu, text);
        Assert.Equal([], analysis.Problems());
        var map = DirectPageMap.Of(analysis, TestContext.Current.CancellationToken);
        var lines = new List<string>();
        foreach (var page in map.Pages)
        {
            lines.Add($"page {(page.Base is { } at ? StateValue.Hex(at, 4) : "?")} [{string.Join(",", page.Segments)}] {page.Relation} hazard={page.IsHazard} used={page.Used} direct={page.Direct}");
            foreach (var overlap in page.Overlaps)
            {
                lines.Add($"  ⧉ {StateValue.Hex(overlap.Page.Base ?? 0, 4)} {StateValue.Hex(overlap.First, 4)}-{StateValue.Hex(overlap.Last, 4)} "
                    + string.Join(";", overlap.Shared.Select(shared => $"{shared.Here.Name}/{shared.There.Name}")));
            }
            foreach (var location in page.Locations)
            {
                lines.Add($"  {location.Symbol.Name} +{location.Offset} x{location.Size} {location.Type} {location.Relation}{(location.IsFixed ? " fixed" : "")}");
                foreach (var use in location.Uses)
                {
                    lines.Add($"    {use.Routine.Name} {use.Role} {use.Accesses.Count}{(use.IsHandler ? " handler" : "")}{(use.IsUnknownPage ? " unknown" : "")}");
                    foreach (var note in use.Hazards)
                        lines.Add($"      {note.Glyph} {note.Text} @ {note.At?.GetText().Trim()}");
                }
            }
            foreach (var unknown in page.Unknown)
                lines.Add($"  ? {unknown.Use.Routine.Name} {unknown.Location.Name} {unknown.Reason} {unknown.Use.Role}");
        }
        return lines;
    }
}
