using Norristown.Flow;
using Norristown.Project;
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
    /// reaches with D at $2100. That page also names each register declared in its range that no
    /// instruction reaches, such as <c>OAMDATA</c>, but not <c>NMITIMEN</c> at $4200, which lies
    /// outside it. The sample shows each kind of sharing: a pointer passed between routines,
    /// a temporary relied on across a call to a routine that uses it too, and a counter that an
    /// interrupt handler bumps. Without a linked configuration its layout is guessed, so it shows
    /// no bytes shared between pages; <see cref="TheConfiguredSampleSharesBytesBetweenPages"/>
    /// shows those.
    /// </summary>
    [Fact]
    public void TheSampleShowsEachKindOfSharing()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Nested hazard=True used=133 direct=13",
                "  ◦ no config · layout guessed",
                "  ptr +0 x2 .addr Shared Guessed",
                "    clear_map Temp 2",
                "    copy_title Out 1",
                "    plot InOut 3",
                "  tmp +2 x2 .word Nested Guessed",
                "    copy_title Temp 4",
                "      ⚠ `jsr plot` runs between a write and a read of it @ jsr plot",
                "      ⚠ `plot` uses it as a temporary @ sty tmp",
                "      ◦ read again here, after the call @ ldx tmp",
                "    plot Temp 2",
                "  frames +4 x1 .byte Interrupt Guessed",
                "    main In 1",
                "    nmi InOut 1 handler unknown",
                "      ⚠ D is left as the interrupted code had it @ ",
                "      ◦ with D = $0000 it reaches $0004, `frames` @ inc frames",
                "      ⚠ with D = $0080 it reaches $0084, `matrix`+4 @ inc frames",
                "      ⚠ with D = $2100 it reaches $2104, `OAMDATA` @ inc frames",
                "  hdma +5 x128 .byte[128] Unused Guessed",
                "page $0080 [M7ZP] Own hazard=False used=8 direct=4",
                "  ◦ no config · layout guessed",
                "  ◦ covers $0100-$01FF, the stack page in emulation mode",
                "  matrix +0 x8 .word[4] Own Guessed",
                "    m7_identity Out 4",
                "page $0100 [NMIZP] Own hazard=False used=2 direct=1",
                "  ◦ no config · layout guessed",
                "  ◦ covers $0100-$01FF, the stack page in emulation mode",
                "  scroll +0 x2 .word Own Guessed",
                "    nmi InOut 1 handler",
                "page $2100 [] Hardware hazard=False used=3 direct=2",
                "  INIDISP +0 x1 .byte Hardware Fixed",
                "    screen_on Write 1",
                "  OAMDATA +4 x1 .byte Hardware Fixed declared",
                "  TM +44 x1 .byte Hardware Fixed",
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
                "  ◦ no config · layout guessed",
                "  count +0 x1 .byte Own Guessed",
                "    main Out 1",
                "  total +1 x1 .byte Unused Guessed",
                "  ptr +251 x2 .addr Own Fixed",
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
                "  ◦ no config · layout guessed",
                "  arg +0 x1 .byte Shared Guessed",
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

    /// <summary>
    /// A routine that writes a location to clear it and then calls a routine that only sets it is
    /// using the call to return a value. The callee does not use the location as a temporary, so
    /// reading it after the call is no hazard.
    /// </summary>
    [Fact]
    public void ACallThatOnlySetsALocationIsNoHazard()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Shared hazard=False used=1 direct=3",
                "  ◦ no config · layout guessed",
                "  flag +0 x1 .byte Shared Guessed",
                "    main Temp 2",
                "    set_flag Out 1",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data flag: .byte
                .segment CODE
                .export .proc main {
                    lda #0
                    sta flag
                    jsr set_flag
                    lda flag
                    rts
                }
                .proc set_flag {
                    lda #1
                    sta flag
                    rts
                }
                """));
    }

    /// <summary>
    /// A routine that relies on a location across a call is a hazard when the callee, or a
    /// routine it calls, writes the location and then reads it back as a temporary of its own.
    /// </summary>
    [Fact]
    public void ACallThatUsesALocationAsATemporaryIsAHazard()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Nested hazard=True used=1 direct=4",
                "  ◦ no config · layout guessed",
                "  tmp +0 x1 .byte Nested Guessed",
                "    main Temp 2",
                "      ⚠ `jsr outer` runs between a write and a read of it @ jsr outer",
                "      ⚠ `inner` uses it as a temporary @ stx tmp",
                "      ◦ read again here, after the call @ lda tmp",
                "    inner Temp 2",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data tmp: .byte
                .segment CODE
                .export .proc main {
                    lda #0
                    sta tmp
                    jsr outer
                    lda tmp
                    rts
                }
                .proc outer {
                    jsr inner
                    rts
                }
                .proc inner {
                    stx tmp
                    lda tmp
                    rts
                }
                """));
    }

    /// <summary>
    /// A helper that both the program and an interrupt handler call can be interrupted part-way
    /// through its use of a location and run again by the handler. A location only that helper
    /// uses is shared with an interrupt, and its use runs both in an interrupt and outside one.
    /// </summary>
    [Fact]
    public void AHelperCalledFromAHandlerAndTheProgramSharesWithAnInterrupt()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Interrupt hazard=True used=1 direct=2",
                "  ◦ no config · layout guessed",
                "  scratch +0 x1 .byte Interrupt Guessed",
                "    util Temp 2 irq main",
                "      ⚠ `util` uses it as a temporary @ sta scratch",
                "      ⚠ and can interrupt itself between its own write and read @ lda scratch",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data scratch: .byte
                .segment CODE
                .export .proc main {
                    jsr util
                    rts
                }
                .proc util {
                    sta scratch
                    lda scratch
                    rts
                }
                .export .proc nmi: interrupt {
                    jsr util
                    rti
                }
                """));
    }

    /// <summary>
    /// A handler that uses a location as a temporary can fire between the program's write of the
    /// location and its read, and change what the program reads. The handler's use is a hazard.
    /// </summary>
    [Fact]
    public void AHandlerThatUsesALocationAsATemporaryIsAHazard()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Interrupt hazard=True used=1 direct=4",
                "  ◦ no config · layout guessed",
                "  tmp +0 x1 .byte Interrupt Guessed",
                "    main Temp 2",
                "    nmi Temp 2 handler",
                "      ⚠ `nmi` uses it as a temporary @ stx tmp",
                "      ⚠ and can interrupt `main` between its write and its read @ lda tmp",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data tmp: .byte
                .segment CODE
                .export .proc main {
                    sta tmp
                    lda tmp
                    rts
                }
                .export .proc nmi: interrupt {
                    stx tmp
                    ldx tmp
                    rti
                }
                """));
    }

    /// <summary>
    /// A handler that keeps the interrupted code's D reaches a location's own page whenever it
    /// interrupts code that holds D there, so its use of the location as a temporary is a hazard
    /// there too. Each page the program holds D at says where the access lands.
    /// </summary>
    [Fact]
    public void AHandlerThroughTheInterruptedDCanBeAHazard()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Interrupt hazard=True used=1 direct=2",
                "  ◦ no config · layout guessed",
                "  tmp +0 x1 .byte Interrupt Guessed",
                "    main Temp 2",
                "    nmi Temp 2 handler unknown",
                "      ⚠ D is left as the interrupted code had it @ ",
                "      ◦ with D = $0000 it reaches $0000, `tmp` @ stx tmp",
                "      ⚠ `nmi` uses it as a temporary @ stx tmp",
                "      ⚠ and can interrupt `main` between its write and its read @ lda tmp",
                "page ? [] Unused hazard=True used=0 direct=0",
                "  ? nmi tmp Interrupted Temp",
            ],
            Render("65816", """
                .segment ZEROPAGE
                .data tmp: .byte
                .segment CODE
                .export .proc main: a8, i8, dp = 0 {
                    sta tmp
                    lda tmp
                    rts
                }
                .export .proc nmi: interrupt, native {
                    sep #$30
                    stx tmp
                    ldx tmp
                    rti
                }
                """));
    }

    /// <summary>
    /// A handler that bumps a counter the program reads is how the two are meant to talk, so it
    /// is shared with an interrupt but no hazard.
    /// </summary>
    [Fact]
    public void AHandlerThatCountsForTheProgramIsNoHazard()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Interrupt hazard=False used=1 direct=2",
                "  ◦ no config · layout guessed",
                "  frames +0 x1 .byte Interrupt Guessed",
                "    main In 1",
                "    nmi InOut 1 handler",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data frames: .byte
                .segment CODE
                .export .proc main {
                @wait:
                    lda frames
                    beq @wait
                    rts
                }
                .export .proc nmi: interrupt {
                    inc frames
                    rti
                }
                """));
    }

    /// <summary>
    /// A handler that saves a location before it uses it and restores it after gives back what
    /// the program wrote, so its use is no hazard.
    /// </summary>
    [Fact]
    public void AHandlerThatSavesAndRestoresALocationIsNoHazard()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Interrupt hazard=False used=1 direct=6",
                "  ◦ no config · layout guessed",
                "  tmp +0 x1 .byte Interrupt Guessed",
                "    main Temp 2",
                "    nmi InOut 4 handler",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data tmp: .byte
                .segment CODE
                .export .proc main {
                    sta tmp
                    lda tmp
                    rts
                }
                .export .proc nmi: interrupt {
                    ldx tmp
                    stx $0100
                    sty tmp
                    ldy tmp
                    ldx $0100
                    stx tmp
                    rti
                }
                """));
    }

    /// <summary>
    /// A write leaves a location set only when it reaches every byte of it. A store into part of an
    /// array, or of one byte into a word, leaves the rest as it was, so a later read of the rest
    /// reads what the routine was given.
    /// </summary>
    [Fact]
    public void OnlyAWriteOfEveryByteSetsALocation()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Own hazard=False used=11 direct=11",
                "  ◦ no config · layout guessed",
                "  buf +0 x4 .byte[4] Own Guessed",
                "    main InOut 2",
                "  wide +4 x2 .word Own Guessed",
                "    main Temp 2",
                "  half +6 x2 .word Own Guessed",
                "    main InOut 2",
                "  pair +8 x2 .word Own Guessed",
                "    main Temp 3",
                "  one +10 x1 .byte Own Guessed",
                "    main Temp 2",
            ],
            Render("65816", """
                .segment ZEROPAGE
                .data buf:  .byte[4]
                .data wide: .word
                .data half: .word
                .data pair: .word
                .data one:  .byte
                .segment CODE
                .export .proc main: a16, i16, dp = 0 {
                    lda #0
                    sta buf
                    lda buf + 1
                    sta wide
                    lda wide
                    sep #$20
                    sta half
                    sta pair
                    sta pair + 1
                    sta one
                    lda one
                    rep #$20
                    lda half
                    lda pair
                    rts
                }
                """));
    }

    /// <summary>
    /// The 6502 sets a pointer one byte at a time. A routine that stores both bytes before it reads
    /// through the pointer uses it as a temporary, and one that stores only the low byte reads the
    /// high byte it was given.
    /// </summary>
    [Fact]
    public void APointerSetOneByteAtATimeIsSet()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Own hazard=False used=4 direct=5",
                "  ◦ no config · layout guessed",
                "  ptr +0 x2 .addr Own Guessed",
                "    main Temp 3",
                "  low +2 x2 .addr Own Guessed",
                "    main InOut 2",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data ptr: .addr
                .data low: .addr
                .segment CODE
                .export .proc main {
                    lda #<main
                    sta ptr
                    lda #>main
                    sta ptr + 1
                    ldy #0
                    lda (ptr),y
                    lda #0
                    sta low
                    lda (low),y
                    rts
                }
                """));
    }

    /// <summary>
    /// A location set one byte at a time is relied on across a call as surely as one set by a
    /// single store, so a call that uses it as a temporary is still a hazard.
    /// </summary>
    [Fact]
    public void AWordSetOneByteAtATimeIsReliedOnAcrossACall()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Nested hazard=True used=2 direct=6",
                "  ◦ no config · layout guessed",
                "  tmp +0 x2 .word Nested Guessed",
                "    main Temp 3",
                "      ⚠ `jsr inner` runs between a write and a read of it @ jsr inner",
                "      ⚠ `inner` uses it as a temporary @ stx tmp",
                "      ◦ read again here, after the call @ lda tmp",
                "    inner Temp 3",
            ],
            Render("6502", """
                .segment ZEROPAGE
                .data tmp: .word
                .segment CODE
                .export .proc main {
                    lda #0
                    sta tmp
                    sta tmp + 1
                    jsr inner
                    lda tmp
                    rts
                }
                .proc inner {
                    stx tmp
                    stx tmp + 1
                    lda tmp
                    rts
                }
                """));
    }

    /// <summary>
    /// A 16-bit store in a routine called writes both bytes of a word on the 65816, so the caller's
    /// later read of the high byte reads what the call set rather than what the caller was given.
    /// </summary>
    [Fact]
    public void ACalledSixteenBitStoreSetsTheHighByte()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Shared hazard=False used=2 direct=3",
                "  ◦ no config · layout guessed",
                "  tmp +0 x2 .word Shared Guessed",
                "    main Temp 2",
                "    fill Out 1",
            ],
            Render("65816", """
                .segment ZEROPAGE
                .data tmp: .word
                .segment CODE
                .export .proc main: a16, i16, dp = 0 {
                    jsr fill
                    sep #$20
                    lda tmp + 1
                    sta tmp
                    rep #$20
                    rts
                }
                .proc fill: a16, i16, dp = 0 {
                    lda #0
                    sta tmp
                    rts
                }
                """));
    }

    /// <summary>
    /// A constant address that an instruction reaches through the zero page takes those bytes as
    /// surely as a declaration does, so it is shown as a location named by its address. A pointer
    /// read through such an address takes both of its bytes. A constant with an index register
    /// added is an offset from wherever the register points, so it names no location.
    /// </summary>
    [Fact]
    public void AConstantAddressIsALocation()
    {
        Assert.Equal(
            [
                "page $0000 [] Own hazard=False used=4 direct=3",
                "  $00FB +251 x1  Own Fixed",
                "    main In 1",
                "  $00FC +252 x1  Own Fixed",
                "    main Out 1",
                "  $00FD +253 x2  Own Fixed",
                "    main In 1",
            ],
            Render("6502", """
                .export .proc main {
                    lda $FB
                    sta $FC
                    ldy #0
                    lda ($FD),y
                    lsr 1,x
                    sta 3,y
                    rts
                }
                """));
    }

    /// <summary>
    /// On the 65816 a constant address lands on the page D reaches. An operand with <c>d:</c> gives
    /// the address itself, and a plain direct operand gives the offset from D, so both of these
    /// reach $2105.
    /// </summary>
    [Fact]
    public void AConstantAddressLandsOnThePageDReaches()
    {
        Assert.Equal(
            [
                "page $2100 [] Own hazard=False used=1 direct=2",
                "  $2105 +5 x1  Own Fixed",
                "    main In 2",
            ],
            Render("65816", """
                .export .proc main: a8, i16, dp = $2100 {
                    lda d:$2105
                    lda $05
                    rts
                }
                """));
    }

    /// <summary>
    /// A linked configuration places zero-page segments where ld65 would. The segments of one
    /// memory area follow one another from its start, a segment with <c>offset</c> begins that far
    /// into the area, and one with <c>start</c> begins at that address.
    /// </summary>
    [Fact]
    public void ALinkedConfigPlacesEachSegment()
    {
        var project = Linked("""
            MEMORY {
                ZP:  start = $0010, size = $00F0;
                ROM: start = $8000, size = $1000;
            }
            SEGMENTS {
                ZEROPAGE: load = ZP, type = zp;
                ZP2:      load = ZP, type = zp, offset = $20;
                ZP3:      load = ZP, type = zp, start = $0080;
                ZP4:      load = ZP, type = zp;
                ZP5:      load = ZP, type = zp, align = $10;
                CODE:     load = ROM, type = ro;
            }
            """);
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE,ZP2,ZP3,ZP4,ZP5] Own hazard=False used=6 direct=6",
                "  one +16 x1 .byte Own Configured",
                "    main Out 1",
                "  two +17 x1 .byte Own Configured",
                "    main Out 1",
                "  three +48 x1 .byte Own Configured",
                "    main Out 1",
                "  four +128 x1 .byte Own Configured",
                "    main Out 1",
                "  five +129 x1 .byte Own Configured",
                "    main Out 1",
                "  six +144 x1 .byte Own Configured",
                "    main Out 1",
            ],
            Render(FlowFragment.Analyze(project, "6502", (Analysis.Path, """
                .segment ZEROPAGE
                .data one: .byte
                .data two: .byte
                .segment ZP2
                .data three: .byte
                .segment ZP3
                .data four: .byte
                .segment ZP4
                .data five: .byte
                .segment ZP5
                .data six: .byte
                .segment CODE
                .export .proc main {
                    sta one
                    sta two
                    sta three
                    sta four
                    sta five
                    sta six
                    rts
                }
                """))));
    }

    /// <summary>
    /// A segment whose predicted addresses its page cannot reach has no offset from D to show. The
    /// page says where the segment lies instead.
    /// </summary>
    [Fact]
    public void ASegmentOutsideItsPageHasNoOffset()
    {
        var project = Linked("""
            MEMORY {
                ZP:  start = $0000, size = $0100;
                ROM: start = $8000, size = $1000;
            }
            SEGMENTS {
                ZEROPAGE: load = ZP, type = zp;
                CODE:     load = ROM, type = ro;
            }
            """, """{ "ZEROPAGE": { "dp": "$0080" } }""");
        Assert.Equal(
            [
                "page $0080 [ZEROPAGE] Own hazard=False used=0 direct=1",
                "  ? `ZEROPAGE` is placed at $0000-$0002, outside this page",
                "  ◦ covers $0100-$01FF, the stack page in emulation mode",
                "  count + x1 .byte Own Configured",
                "    main Out 1",
                "  ptr + x2 .addr Unused Configured",
            ],
            Render(FlowFragment.Analyze(project, "65816", (Analysis.Path, """
                .segment ZEROPAGE
                .data count: .byte
                .data ptr: .addr
                .segment CODE
                .export .proc main: a8, dp = $0080 {
                    stz count
                    rts
                }
                """))));
    }

    /// <summary>
    /// The order of the files on ld65's command line decides the order of their bytes in a
    /// segment, and nt65 does not run ld65. A page whose segment several files fill says so.
    /// </summary>
    [Fact]
    public void TheOrderOfFilesInASegmentIsAGuess()
    {
        var project = Linked("""
            MEMORY {
                ZP:  start = $0000, size = $0100;
                ROM: start = $8000, size = $1000;
            }
            SEGMENTS {
                ZEROPAGE: load = ZP, type = zp;
                CODE:     load = ROM, type = ro;
            }
            """);
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Own hazard=False used=2 direct=2",
                "  ◦ `ZEROPAGE` has data in several files, whose order is a guess",
                "  first +0 x1 .byte Own Configured",
                "    main Out 1",
                "  second +1 x1 .byte Own Configured",
                "    main Out 1",
            ],
            Render(FlowFragment.Analyze(project, "6502",
                ("lib.nt65", """
                    .segment ZEROPAGE
                    .export .data first: .byte
                    """),
                (Analysis.Path, """
                    .use lib::first
                    .segment ZEROPAGE
                    .data second: .byte
                    .segment CODE
                    .export .proc main {
                        sta first
                        sta second
                        rts
                    }
                    """))));
    }

    /// <summary>
    /// The sample's pages laid out by a linked configuration share bytes where the configuration
    /// puts them, so the array on the zero page takes the bytes of the matrix on the page at $0080.
    /// </summary>
    [Fact]
    public void TheConfiguredSampleSharesBytesBetweenPages()
    {
        var project = Linked("""
            MEMORY {
                ZP:   start = $0000, size = $0100;
                M7:   start = $0080, size = $0080;
                NMI:  start = $0100, size = $0080;
                WRAM: start = $0200, size = $1E00;
                ROM:  start = $8000, size = $8000;
            }
            SEGMENTS {
                ZEROPAGE: load = ZP, type = zp;
                M7ZP:     load = M7, type = zp;
                NMIZP:    load = NMI, type = zp;
                BSS:      load = WRAM, type = bss;
                CODE:     load = ROM, type = ro;
            }
            """, """{ "M7ZP": { "dp": "$0080" }, "NMIZP": { "dp": "$0100" } }""");
        var text = Snes.Replace(".segment M7ZP: zp, dp = $0080\n", "", StringComparison.Ordinal)
            .Replace(".segment NMIZP: zp, dp = $0100\n", "", StringComparison.Ordinal);
        var lines = Render(FlowFragment.Analyze(project, "65816", (Analysis.Path, text)));
        Assert.Equal(
            [
                "  ⧉ $0080 $0080-$00ff hdma/matrix",
                "  ⧉ $0000 $0080-$00ff matrix/hdma",
                "  ⧉ $0100 $0100-$017f ",
                "  ⧉ $0080 $0100-$017f ",
            ],
            lines.Where(line => line.StartsWith("  ⧉", StringComparison.Ordinal)));
        Assert.Contains("  hdma +5 x128 .byte[128] Unused Configured", lines);
    }

    /// <summary>
    /// A symbol whose address the last build gives is laid out there rather than where it is
    /// predicted, and the rest of its segment is still predicted.
    /// </summary>
    [Fact]
    public void TheLastBuildGivesAddresses()
    {
        var analysis = FlowFragment.Analyze("6502", """
            .segment ZEROPAGE
            .data count: .byte
            .data total: .byte
            .segment CODE
            .export .proc main {
                sta count
                sta total
                rts
            }
            """);
        var built = new Dictionary<Symbol, long> { [analysis.File(Analysis.Path).Symbol("count")] = 0x40 };
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Own hazard=False used=2 direct=2",
                "  ◦ no config · layout guessed",
                "  total +1 x1 .byte Own Guessed",
                "    main Out 1",
                "  count +64 x1 .byte Own Built",
                "    main Out 1",
            ],
            Render(analysis, built));
    }

    /// <summary>
    /// An address alias that falls inside a segment's predicted bytes takes the same bytes as the
    /// data laid out there. The layout chose those addresses, so the two collide, and the page
    /// notes each collision.
    /// </summary>
    [Fact]
    public void AnAliasInsideASegmentCollidesWithItsData()
    {
        var project = Linked("""
            MEMORY {
                ZP:  start = $0080, size = $0080;
                ROM: start = $8000, size = $1000;
            }
            SEGMENTS {
                ZEROPAGE: load = ZP, type = zp;
                CODE:     load = ROM, type = ro;
            }
            """);
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Own hazard=False used=3 direct=3",
                "  ⧉ `count` and `ptr` share $0080 unintentionally",
                "  ⧉ `ptr` and `total` share $0081 unintentionally",
                "  count +128 x1 .byte Own Configured",
                "    ⧉ Collision ptr $0080-$0080",
                "    main Out 1",
                "  ptr +128 x2 .addr Own Fixed",
                "    ⧉ Collision count $0080-$0080",
                "    ⧉ Collision total $0081-$0081",
                "    main In 1",
                "  total +129 x2 .word Own Configured",
                "    ⧉ Collision ptr $0081-$0081",
                "    main Out 1",
            ],
            Render(FlowFragment.Analyze(project, "6502", (Analysis.Path, """
                .data ptr: .addr = $80
                .segment ZEROPAGE
                .data count: .byte
                .data total: .word
                .segment CODE
                .export .proc main {
                    sta count
                    sta total
                    ldy #0
                    lda (ptr),y
                    rts
                }
                """))));
    }

    /// <summary>
    /// Two zero-page segments that a configuration runs in different memory areas whose ranges
    /// overlap share bytes because the configuration says so. Each block of msbasic's zero page is
    /// laid out this way, so the overlap is authored, and the page does not note it.
    /// </summary>
    [Fact]
    public void SegmentsInOverlappingAreasShareBytesByConfig()
    {
        var project = Linked("""
            MEMORY {
                ZP1: start = $0000, size = $0100;
                ZP2: start = $0002, size = $00FE;
                ROM: start = $8000, size = $1000;
            }
            SEGMENTS {
                ZP1:  load = ZP1, type = zp;
                ZP2:  load = ZP2, type = zp;
                CODE: load = ROM, type = ro;
            }
            """);
        Assert.Equal(
            [
                "page $0000 [ZP1,ZP2] Own hazard=False used=4 direct=2",
                "  vector +0 x4 .byte[4] Own Configured",
                "    ⧉ Authored count $0002-$0003",
                "    main Out 1",
                "  count +2 x2 .word Own Configured",
                "    ⧉ Authored vector $0002-$0003",
                "    main Out 1",
            ],
            Render(FlowFragment.Analyze(project, "6502", (Analysis.Path, """
                .segment ZP1
                .data vector: .byte[4]
                .segment ZP2
                .data count: .word
                .segment CODE
                .export .proc main {
                    sta vector
                    sta count
                    rts
                }
                """))));
    }

    /// <summary>
    /// Two segments whose starts the configuration pins to overlapping addresses share bytes as the
    /// configuration says, even in one memory area. A segment that the configuration places only
    /// after the ones before it has bytes that run on into the next one by accident, so the two
    /// collide, and the page notes it.
    /// </summary>
    [Fact]
    public void PinnedSegmentsShareBytesByConfigAndAFollowingOneCollides()
    {
        var project = Linked("""
            MEMORY {
                ZP:  start = $0000, size = $0100;
                ROM: start = $8000, size = $1000;
            }
            SEGMENTS {
                ZEROPAGE: load = ZP, type = zp;
                ZP2:      load = ZP, type = zp, start = $0002;
                ZP3:      load = ZP, type = zp, start = $0040;
                ZP4:      load = ZP, type = zp, start = $0041;
                CODE:     load = ROM, type = ro;
            }
            """);
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE,ZP2,ZP3,ZP4] Own hazard=False used=5 direct=4",
                "  ⧉ `head` and `pinned` share $0002 unintentionally",
                "  head +0 x3 .byte[3] Own Configured",
                "    ⧉ Collision pinned $0002-$0002",
                "    main Out 1",
                "  pinned +2 x1 .byte Own Configured",
                "    ⧉ Collision head $0002-$0002",
                "    main Out 1",
                "  first +64 x2 .word Own Configured",
                "    ⧉ Authored second $0041-$0041",
                "    main Out 1",
                "  second +65 x1 .byte Own Configured",
                "    ⧉ Authored first $0041-$0041",
                "    main Out 1",
            ],
            Render(FlowFragment.Analyze(project, "6502", (Analysis.Path, """
                .segment ZEROPAGE
                .data head: .byte[3]
                .segment ZP2
                .data pinned: .byte
                .segment ZP3
                .data first: .word
                .segment ZP4
                .data second: .byte
                .segment CODE
                .export .proc main {
                    sta head
                    sta pinned
                    sta first
                    sta second
                    rts
                }
                """))));
    }

    /// <summary>
    /// msbasic's zero page is four blocks whose memory areas overlap on purpose, and its CBM BASIC 2
    /// configuration overlaps the first block with the second and third. Every run of bytes that
    /// two of its segments' locations share is authored by the configuration, so the page notes
    /// none of them. Only that configuration is analyzed, to keep the test quick.
    /// </summary>
    [Fact]
    public void MsbasicsZeroPageOverlapsByConfig()
    {
        var directory = Repo.Path("examples", "msbasic");
        var root = Repo.ReadProject(directory);
        var configuration = root.Configurations.Single(item => item.Name == "cbmbasic2");
        var project = root.Configured(configuration.Name, configuration.Declaration);
        var sources = project.Files.SelectMany(glob => SourceGlobs.Matching(directory, glob))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(path => new SourceFile(path, Repo.ReadText(Path.GetFullPath(path, directory))))
            .ToList();
        var map = DirectPageMap.Of(Compiler.Analyze(sources, project, _ => null), null, TestContext.Current.CancellationToken);
        var zero = Assert.Single(map.Pages, page => page.Base == 0);
        var laid = zero.Locations.Where(location => location.Layout != PageLayout.Fixed).ToDictionary(location => location.Name);
        var shared = laid.Values.SelectMany(location => location.Shared).Where(bytes => laid.ContainsKey(bytes.There)).ToList();
        Assert.NotEmpty(shared);
        Assert.All(shared, bytes => Assert.Equal(SharedBytesKind.Authored, bytes.Kind));
        Assert.DoesNotContain(zero.Notes, note => note.Glyph == "⧉" && laid.Keys.Count(name => note.Text.Contains($"`{name}`", StringComparison.Ordinal)) == 2);
    }

    /// <summary>
    /// Two address aliases on one byte name it two ways on purpose, as msbasic's zero page does, so
    /// they share the byte without a note.
    /// </summary>
    [Fact]
    public void TwoAliasesOnOneByteAreDeliberate()
    {
        Assert.Equal(
            [
                "page $0000 [] Own hazard=False used=2 direct=2",
                "  low +251 x1 .byte Own Fixed",
                "    ⧉ Deliberate ptr $00fb-$00fb",
                "    main Out 1",
                "  ptr +251 x2 .addr Own Fixed",
                "    ⧉ Deliberate low $00fb-$00fb",
                "    main In 1",
            ],
            Render("6502", """
                .data ptr: .addr = $FB
                .data low: .byte = $FB
                .export .proc main {
                    sta low
                    ldy #0
                    lda (ptr),y
                    rts
                }
                """));
    }

    /// <summary>
    /// A location whose address the program takes is used through a pointer or an index that the
    /// map cannot follow. It is still unused by any instruction of its own, but it carries the
    /// statements that take its address. Those are an immediate operand, with or without
    /// <c>&lt;</c> and <c>&gt;</c>, a value in a data table, and an argument a macro puts in one.
    /// A macro in the location's own file is shown at the line of its body, as its accesses are.
    /// </summary>
    [Fact]
    public void ALocationWhoseAddressIsTakenIsReferenced()
    {
        Assert.Equal(
            [
                "page $0000 [ZEROPAGE] Unused hazard=False used=9 direct=0",
                "  ◦ no config · layout guessed",
                "  ptr +0 x2 .addr Unused Guessed",
                "    ◎ lda #<ptr",
                "    ◎ ldx #>ptr",
                "  tmp +2 x1 .byte Unused Guessed",
                "    ◎ ldy #tmp",
                "  buf +3 x4 .byte[4] Unused Guessed",
                "    ◎ .data table: .addr buf",
                "  out +7 x1 .byte Unused Guessed",
                "    ◎ ldx #.lobyte(p)",
                "  idle +8 x1 .byte Unused Guessed",
            ],
            Render("6502", """
                .macro point(p: expr) {
                    ldx #.lobyte(p)
                }
                .segment ZEROPAGE
                .data ptr: .addr
                .data tmp: .byte
                .data buf: .byte[4]
                .data out: .byte
                .data idle: .byte
                .segment CODE
                .export .proc main {
                    lda #<ptr
                    ldx #>ptr
                    ldy #tmp
                    point!(out)
                    rts
                }
                .segment RODATA
                .data table: .addr buf
                """));
    }

    /// <summary>Returns the map of <paramref name="text"/> as lines of text, one for each page, location, use and note.</summary>
    private static List<string> Render(string cpu, string text) => Render(FlowFragment.Analyze(cpu, text));

    /// <summary>
    /// Returns the map of <paramref name="analysis"/>'s program as lines of text, one for each
    /// page, location, use and note, with the addresses in <paramref name="built"/> as the last
    /// build's.
    /// </summary>
    private static List<string> Render(ProgramAnalysis analysis, IReadOnlyDictionary<Symbol, long>? built = null)
    {
        Assert.Equal([], analysis.Problems());
        var map = DirectPageMap.Of(analysis, built, TestContext.Current.CancellationToken);
        var lines = new List<string>();
        foreach (var page in map.Pages)
        {
            lines.Add($"page {(page.Base is { } at ? StateValue.Hex(at, 4) : "?")} [{string.Join(",", page.Segments)}] {page.Relation} hazard={page.IsHazard} used={page.Used} direct={page.Direct}");
            foreach (var note in page.Notes)
                lines.Add($"  {note.Glyph} {note.Text}");
            foreach (var overlap in page.Overlaps)
            {
                lines.Add($"  ⧉ {StateValue.Hex(overlap.Page.Base ?? 0, 4)} {StateValue.Hex(overlap.First, 4)}-{StateValue.Hex(overlap.Last, 4)} "
                    + string.Join(";", overlap.Shared.Select(shared => $"{shared.Here}/{shared.There}")));
            }
            foreach (var location in page.Locations)
            {
                lines.Add($"  {location.Name} +{location.Offset} x{location.Size} {location.Type} {location.Relation} {location.Layout}"
                    + (location.IsHardware && !location.IsReached ? " declared" : ""));
                foreach (var shared in location.Shared)
                    lines.Add($"    ⧉ {shared.Kind} {shared.There} {StateValue.Hex(shared.First, 4)}-{StateValue.Hex(shared.Last, 4)}");
                foreach (var reference in location.References)
                    lines.Add($"    ◎ {reference.GetText().Trim()}");
                foreach (var use in location.Uses)
                {
                    lines.Add($"    {use.Routine.Name} {use.Role} {use.Accesses.Count}{(use.IsHandler ? " handler" : "")}{(use.IsUnknownPage ? " unknown" : "")}"
                        + $"{(use.InInterrupt && !use.IsHandler ? " irq" : "")}{(use.InInterrupt && use.InMain ? " main" : "")}");
                    foreach (var note in use.Hazards)
                        lines.Add($"      {note.Glyph} {note.Text} @ {note.At?.GetText().Trim()}");
                }
            }
            foreach (var unknown in page.Unknown)
                lines.Add($"  ? {unknown.Use.Routine.Name} {unknown.Location.Name} {unknown.Reason} {unknown.Use.Role}");
        }
        return lines;
    }

    /// <summary>
    /// Returns the settings of a project that links <paramref name="config"/>, with the project
    /// file's <c>segments</c> given by <paramref name="segments"/>.
    /// </summary>
    private static ProjectSettings Linked(string config, string segments = "{}")
    {
        var project = ProjectFile.Read(
            ProjectFile.Name,
            $$"""{ "links": { "rom": { "config": "rom.cfg" } }, "segments": {{segments}} }""",
            path => path == "rom.cfg" ? config : null);
        Assert.Empty(project.Diagnostics);
        return project with { Severities = Analysis.Fragment.Severities };
    }
}
