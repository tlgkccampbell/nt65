using Norristown.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what an editor gets within one file, which is hover, go to definition, find references,
/// highlights and rename.
/// </summary>
public sealed class SymbolRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A file with zero-page data, a constant, a scope and two procs, so that every request has
    /// something of each kind to find.
    /// </summary>
    private const string Source = """
        .module main
        .segment ZEROPAGE {
        .data ptr:    .word
        }

        .const SCREEN = $0400
        .segment CODE
        .scope gfx {
            .proc init {
                lda #0
                sta ptr
            @loop:
                bne @loop
                rts
            }
        }

        .export .proc main {
            jsr gfx::init
            lda ptr
            lda #<SCREEN
            rts
        }
        """;

    [Fact]
    public async Task AnnouncesWhatTheNameLayerCanDo()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);

        var capabilities = client.Initialized.Capabilities;
        Assert.True(capabilities.HoverProvider);
        Assert.True(capabilities.DefinitionProvider);
        Assert.True(capabilities.ReferencesProvider);
        Assert.True(capabilities.DocumentHighlightProvider);
        Assert.True(capabilities.RenameProvider?.PrepareProvider);
    }

    /// <summary>Hover says what a symbol is, what its value is, and how wide an address it makes.</summary>
    [Fact]
    public async Task HoverDescribesALabelAndAConstant()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var label = await client.HoverAsync(Uri, Locate.At(Source, ".data |ptr"), timeout);
        Assert.NotNull(label);
        Assert.Contains("```nt65\n.data ptr:    .word\n```", label.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("address  zp (1 byte) in ZEROPAGE", label.Contents.Value, StringComparison.Ordinal);

        var constant = await client.HoverAsync(Uri, Locate.At(Source, "SCREEN ="), timeout);
        Assert.NotNull(constant);
        Assert.Contains("```nt65\n.const SCREEN = $0400\n```", constant.Contents.Value, StringComparison.Ordinal);

        // A constant's value is what a reader hovers it for, so it comes above the rule, with
        // whether the configuration decides it. How wide an address it would make is supporting
        // detail, so it comes below the rule.
        Assert.Contains("value    $0400 (1024)\nknown    decided by the configuration\n```\n---\n", constant.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("address  abs (2 bytes)", constant.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks that the hover's headline, which drops the comment at the end of the declaring
    /// line, does not mistake a <c>;</c> inside a quoted character or string for the start of
    /// that comment.
    /// </summary>
    [Fact]
    public async Task HoverKeepsASemicolonThatIsQuoted()
    {
        var timeout = TestTimeout.Token();
        const string Text =
            ".module main\n.const SEMI = ';' ; the separator\n.segment RODATA\n.data text: .byte \"a\\\";b\" ; escaped\n";
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var character = await client.HoverAsync(Uri, Locate.At(Text, "SEMI ="), timeout);
        Assert.NotNull(character);
        Assert.Contains("```nt65\n.const SEMI = ';'\n```", character.Contents.Value, StringComparison.Ordinal);

        var text = await client.HoverAsync(Uri, Locate.At(Text, ".data t|ext:"), timeout);
        Assert.NotNull(text);
        Assert.Contains("```nt65\n.data text: .byte \"a\\\";b\"\n```", text.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The only names the output spells differently from the source are those ca65 would read as
    /// an instruction, which the emitter prefixes with the module's name. Hover mentions the output
    /// name for those names only, because every other name keeps its spelling in the output, so
    /// mentioning it would add nothing.
    /// </summary>
    [Fact]
    public async Task HoverShowsWhatTheOutputCallsANameCa65WouldMisread()
    {
        var timeout = TestTimeout.Token();
        const string Text = ".module main\n.cpu 6502\n.segment CODE\n.data lda: .byte 0\n.data plain: .byte 0\n";
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var misread = await client.HoverAsync(Uri, Locate.At(Text, ".data |lda"), timeout);
        var plain = await client.HoverAsync(Uri, Locate.At(Text, ".data |plain"), timeout);

        Assert.Contains("`main__lda`", misread?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("in the output", misread?.Contents.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("in the output", plain?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The distance between two places in one data declaration is a constant, and hover gives its
    /// value, as it does for any other constant. An error number that is a message's offset in a
    /// table is a number, not an address.
    /// </summary>
    [Fact]
    public async Task HoverGivesTheValueOfADistanceInsideADeclaration()
    {
        var timeout = TestTimeout.Token();
        const string Text =
            ".module main\n.cpu 6502\n.segment RODATA\n.data messages {\n    .data first: .byte 1, 2, 3\n"
                + "    .data second: .byte 4\n}\n.const ERR_SECOND = messages::second - messages\n";
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var hover = await client.HoverAsync(Uri, Locate.At(Text, "ERR_SECOND ="), timeout);

        Assert.Contains("value    3\n", hover?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("address  zp (1 byte)", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Data found elsewhere that states no element type takes the one at its address, which its
    /// declaration does not show, so hover shows it. A name whose address has none says so.
    /// </summary>
    [Fact]
    public async Task HoverGivesTheElementTypeThatDataFoundElsewhereTakes()
    {
        var timeout = TestTimeout.Token();
        const string Text =
            ".module main\n.segment ZEROPAGE\n.data FAC: .byte[5]\n.data STRNG1: .word\n"
                + ".data FAC_LAST = FAC + 4\n.data EXT = STRNG1 + 1\n";
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var last = await client.HoverAsync(Uri, Locate.At(Text, "FAC_LAST ="), timeout);
        var ext = await client.HoverAsync(Uri, Locate.At(Text, "EXT ="), timeout);

        Assert.Contains("element  .byte\n", last?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("size     1 byte\n", last?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("element  none, so it has no size\n", ext?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A function whose body produces text evaluates to that text where it is called, and hover on
    /// the call shows it, with any byte that is not a printable character escaped as a string
    /// literal would show it. A text constant defined by such a call is shown as text as well.
    /// </summary>
    [Fact]
    public async Task HoverGivesTheTextACallIsWorth()
    {
        var timeout = TestTimeout.Token();
        const string Text =
            ".module main\n.cpu 6502\n"
                + ".func htasc(text) = .strcat(.strsub(text, 0, .strlen(text) - 1), .strat(text, .strlen(text) - 1) | $80)\n"
                + ".const GREETING = htasc(\"HI\")\n.segment RODATA\n.data t: .byte htasc(\"OK\")\n";
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var call = await client.HoverAsync(Uri, Locate.At(Text, ".byte h|tasc(\"OK\")"), timeout);
        var constant = await client.HoverAsync(Uri, Locate.At(Text, "GREETING ="), timeout);

        Assert.Contains("value  \"O\\xcb\"", call?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("\"H\\xc9\"", constant?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover shows a value in hexadecimal, which is how an address or a mask is read. A value may
    /// also be a count, so its decimal is put beside it, except below ten, where the two are the
    /// same digit and there is nothing to add.
    /// </summary>
    [Fact]
    public async Task HoverPutsTheDecimalBesideAHexadecimalValue()
    {
        var timeout = TestTimeout.Token();
        const string Text = ".module main\n.const WIDE = $0400\n.const SMALL = 4\n";
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var wide = await client.HoverAsync(Uri, Locate.At(Text, "WIDE ="), timeout);
        var small = await client.HoverAsync(Uri, Locate.At(Text, "SMALL ="), timeout);

        Assert.Contains("value    $0400 (1024)", wide?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("value    4\n", small?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>Hover shows a name inside a scope under the qualified path another file would use.</summary>
    [Fact]
    public async Task HoverQualifiesAScopedName()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, Locate.At(Source, ".proc i|nit"), timeout);
        Assert.NotNull(hover);
        Assert.Contains("```nt65\n.proc gfx::init\n```", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Equal(Locate.Span(Source, ".proc |init"), hover.Range);
    }

    /// <summary>
    /// Hover says whether the configuration decides a constant, so that an <c>.if</c> may test
    /// it, and whether a setting has the value the build gave it or its default.
    /// </summary>
    [Fact]
    public async Task HoverSaysWhatTheConfigurationDecides()
    {
        const string Text = ".module main\n.export WIDE, BIG, SIZE\n.const WIDE ?= 0\n.const BIG = WIDE * 2\n"
            + ".segment BSS\n.data table: .byte[4]\n.const SIZE = .sizeof(table)\n";
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var setting = await client.HoverAsync(Uri, Locate.At(Text, ".const W|IDE"), timeout);
        var decided = await client.HoverAsync(Uri, Locate.At(Text, ".const B|IG"), timeout);
        var measured = await client.HoverAsync(Uri, Locate.At(Text, ".const S|IZE"), timeout);

        Assert.Contains("its default, which the build may set", setting?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("decided by the configuration", decided?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("once the declarations are read", measured?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a routine costs and which registers it preserves are shown wherever its name appears.
    /// A reader asks what a call costs at the call, not at the declaration, and the lens that
    /// shows the cost above the declaration may be far from the call.
    /// </summary>
    [Fact]
    public async Task HoverOnACallShowsWhatTheRoutineCostsAndKeeps()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, Locate.At(Source, "jsr gfx::|init"), timeout);

        Assert.NotNull(hover);
        Assert.Contains("```nt65\n.proc gfx::init\n```", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("cost       13+ cycles, loops\npreserves  X, Y, C", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover over a type and its members shows what an editor needs to know about a layout, which
    /// is the offset a member sits at, how much room it takes, and its type.
    /// </summary>
    [Fact]
    public async Task HoverOnALayoutShowsOffsetsAndSizes()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .struct Point {
            x:      .word
            y:      .word
            }

            .struct Player {
            pos:    .type Point
            hp:     .byte
            }

            .data here:   .type Player[4]
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var member = await client.HoverAsync(Uri, Locate.At(Text, "hp:"), timeout);
        Assert.NotNull(member);
        Assert.Contains("```nt65\nmember Player::hp\n```", member.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("offset  4\n```\n---\n", member.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("size    1 byte", member.Contents.Value, StringComparison.Ordinal);

        var nested = await client.HoverAsync(Uri, Locate.At(Text, "pos:"), timeout);
        Assert.NotNull(nested);
        Assert.Contains("type    Point\nsize    4 bytes", nested.Contents.Value, StringComparison.Ordinal);

        // An array's element size and its count are shown together, as one fact.
        var array = await client.HoverAsync(Uri, Locate.At(Text, ".data |here"), timeout);
        Assert.NotNull(array);
        Assert.Contains("```nt65\n.data here:   .type Player[4]\n```", array.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("size  20 bytes x 4", array.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>A cheap local has no path, so hover names the routine it is private to.</summary>
    [Fact]
    public async Task HoverOnACheapLocalShowsWhereItLives()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, Locate.At(Source, "@loop:"), timeout);
        Assert.NotNull(hover);
        Assert.Contains("```nt65\n@loop:\n```", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("private to  init", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every hover is laid out to be read from the top down. It starts with the line that declares
    /// the thing, then the comment its author left above it, then the one or two facts most often
    /// wanted about that kind of name, and then a rule with everything else under it. Nothing is
    /// left out for being far down.
    /// </summary>
    [Fact]
    public async Task HoverLeadsWithWhatThatKindOfNameIsAskedAbout()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
            .module main
            .segment CODE

            ; How many tiles a row holds.
            .const WIDTH = 8 * 4

            .proc clear {
                rts
            }

            ; Writes the character in A.
            .proc CHROUT = $ffd2
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var constant = await client.HoverAsync(Uri, Locate.At(Text, "WIDTH ="), timeout);
        Assert.Equal("""
            ```nt65
            .const WIDTH = 8 * 4
            ```

            How many tiles a row holds.

            ```nt65-hover
            value    $20 (32)
            known    decided by the configuration
            ```
            ---
            ```nt65-hover
            address  zp (1 byte)
            ```
            """.ReplaceLineEndings("\n"), constant!.Contents.Value);

        // A routine is hovered to find out what a call to it costs and which registers it preserves.
        var routine = await client.HoverAsync(Uri, Locate.At(Text, ".proc |clear"), timeout);
        Assert.Equal("""
            ```nt65
            .proc clear
            ```

            ```nt65-hover
            cost       6 cycles
            preserves  A, X, Y, C
            ```
            ---
            ```nt65-hover
            address    abs (2 bytes) in CODE
            ```
            """.ReplaceLineEndings("\n"), routine!.Contents.Value);

        // A routine with no body has no cost to lead with, and a rule would separate nothing,
        // so the rest follows the comment as the leading rows would.
        var bodiless = await client.HoverAsync(Uri, Locate.At(Text, ".proc |CHROUT"), timeout);
        Assert.Equal("""
            ```nt65
            .proc CHROUT = $ffd2
            ```

            Writes the character in A.

            ```nt65-hover
            value    $ffd2 (65490)
            address  abs (2 bytes)
            ```
            """.ReplaceLineEndings("\n"), bodiless!.Contents.Value);
    }

    [Fact]
    public async Task HoverOnSomethingThatIsNeitherANameNorAnInstructionShowsNothing()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        Assert.Null(await client.HoverAsync(Uri, Locate.At(Source, "rts\n    |}"), timeout));
    }

    /// <summary>
    /// Hover on an instruction, away from any name in it, shows how many cycles it takes and how
    /// many the block around it takes. The count is a range wherever it depends on something the
    /// program does not determine.
    /// </summary>
    [Fact]
    public async Task HoverOnAnInstructionShowsWhatItCosts()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, Locate.At(Source, "lda #0"), timeout);
        Assert.NotNull(hover);
        Assert.Contains("```nt65\nlda #0  ; load accumulator\n```", hover.Contents.Value, StringComparison.Ordinal);

        // `lda #0` and `sta z:ptr` come to five, and the block ends at the label after them.
        Assert.Contains("cycles  2         block 5", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>A branch costs two not taken and three taken, and one more when a taken
    /// branch crosses a page, so what it costs is an interval.</summary>
    [Fact]
    public async Task HoverOnABranchShowsTheWholeInterval()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, Locate.At(Source, "bne @loop"), timeout);
        Assert.NotNull(hover);
        Assert.Contains("cycles  2-4       block 2-4", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// On the 65816, hover on an instruction also shows the processor state that reaches it,
    /// which is what decided the size of its immediate.
    /// </summary>
    [Fact]
    public async Task HoverOnA65816InstructionShowsTheStateReachingIt()
    {
        var timeout = TestTimeout.Token();
        const string Text =
            ".module main\n.cpu 65816\n.segment CODE\n.export .proc p: a16, i8 {\n"
                + "    php\n    lda #$1234\n    plp\n    rts\n}\n";
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Text);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        var hover = await client.HoverAsync(Uri, Locate.At(Text, "l|da #$1234"), timeout);
        Assert.NotNull(hover);
        Assert.Contains("cycles  3", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("state   a16, i8, native", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover shows an instruction with the name its datasheet gives it, as a trailing comment. A
    /// reader who already knows what <c>pea</c> stands for would not be hovering over it.
    /// </summary>
    [Fact]
    public async Task HoverNamesTheInstructionOnTheHeadline()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var load = await client.HoverAsync(Uri, Locate.At(Source, "lda #0"), timeout);
        Assert.Contains("```nt65\nlda #0  ; load accumulator\n```", load?.Contents.Value, StringComparison.Ordinal);

        const string Text =
            ".module main\n.cpu 65816\n.segment CODE\n.export .proc p: a8, i8 {\n    pea $1234\n    pld\n    rts\n}\n";
        await using var wide = await TestClient.OpenedAsync(timeout, (Uri, Text));

        var push = await wide.HoverAsync(Uri, Locate.At(Text, "pea $1234"), timeout);
        Assert.Contains(
            "```nt65\npea $1234  ; push effective absolute address\n```",
            push?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Which flags an instruction writes is the fact people misremember, so it is beside the
    /// instruction rather than in a datasheet on the desk.
    /// </summary>
    [Fact]
    public async Task HoverListsTheFlagsAnInstructionWrites()
    {
        var timeout = TestTimeout.Token();
        const string Text =
            ".module main\n.segment CODE\n.export .proc main {\n    lda #1\n    adc #2\n    sta $10\n    rts\n}\n";
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Text);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        var load = await client.HoverAsync(Uri, Locate.At(Text, "lda #1"), timeout);
        var add = await client.HoverAsync(Uri, Locate.At(Text, "adc #2"), timeout);
        var store = await client.HoverAsync(Uri, Locate.At(Text, "sta $10"), timeout);

        // The line's cost is what a reader hovers an instruction for, so it comes above the rule.
        // The flags it writes and what the registers hold are supporting detail below it. The
        // grid is fenced as a language of its own so that an editor can tell one row from another.
        Assert.Contains("```nt65-hover\ncycles  2         block 13\n```\n---\n", load?.Contents.Value,
            StringComparison.Ordinal);
        Assert.Contains("```nt65-hover\nflags   N Z\n\nA       as entered\n",
            load?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("flags   N V Z C\n", add?.Contents.Value, StringComparison.Ordinal);

        // A store writes no flag at all, so the flags row is left out rather than saying none.
        Assert.DoesNotContain("flags", store?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cycle count that is a range says what the extra cycles at its top are for. The reason is
    /// always something decided as the processor runs that the program does not state, and a
    /// reader left to work out which it is has been given only half an answer.
    /// </summary>
    [Fact]
    public async Task HoverExplainsWhyACountIsAnInterval()
    {
        var timeout = TestTimeout.Token();
        const string Indexed = """
            .module main
            .segment CODE
            .data table: .byte[300]
            .export .proc main {
                ldx #4
            @loop:
                lda table,x
                dex
                bne @loop
                rts
            }
            .export table
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Indexed.ReplaceLineEndings("\n")));

        var read = await client.HoverAsync(Uri, Locate.At(Indexed, "lda table,x"), timeout);
        var branch = await client.HoverAsync(Uri, Locate.At(Indexed, "bne @loop"), timeout);

        Assert.Contains(
            "cycles  4-5       block 8-11    +1 when the read crosses a page\n",
            read?.Contents.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "cycles  2-4       block 8-11    +1 when taken, +1 when that crosses a page\n",
            branch?.Contents.Value,
            StringComparison.Ordinal);

        // On the 65816 a direct-page operand costs one more cycle when the low byte of D is not
        // zero, and a routine that declares nothing about D leaves that unknown.
        const string DirectPage =
            ".module main\n.cpu 65816\n.segment CODE\n.export .proc p: a8, i8 {\n    lda $10\n    rts\n}\n";
        await using var wide = await TestClient.OpenedAsync(timeout, (Uri, DirectPage));

        var direct = await wide.HoverAsync(Uri, Locate.At(DirectPage, "lda $10"), timeout);

        Assert.Contains(
            "cycles  3-4       block 9-10    +1 when the low byte of D is not zero\n",
            direct?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>Hover on an <c>.ensure</c> shows the instructions the analysis found it must emit.</summary>
    [Fact]
    public async Task HoverOnAnEnsureShowsWhatItWrites()
    {
        var timeout = TestTimeout.Token();
        const string Text =
            ".module main\n.cpu 65816\n.segment CODE\n.export .proc p: a8 -> a16, i8 {\n"
                + "    .ensure a16, i8\n    rts\n}\n";
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Text);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        var hover = await client.HoverAsync(Uri, Locate.At(Text, ".|ensure"), timeout);
        Assert.NotNull(hover);
        Assert.Contains("writes  rep #$20 and sep #$10", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefinitionGoesToTheDeclaration()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // `ptr` used on the `lda ptr` line, declared on line 2.
        var definition = await client.DefinitionAsync(Uri, Locate.At(Source, "lda |ptr"), timeout);
        Assert.NotNull(definition);
        Assert.Equal(Uri, definition.Uri);
        Assert.Equal(Locate.Span(Source, ".data |ptr"), definition.Range);
    }

    /// <summary>Each part of a path finds its own declaration.</summary>
    [Fact]
    public async Task DefinitionFollowsEachPartOfAPath()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var scope = await client.DefinitionAsync(Uri, Locate.At(Source, "jsr |gfx"), timeout);
        Assert.Equal(Locate.Span(Source, ".scope |gfx"), scope?.Range);

        var routine = await client.DefinitionAsync(Uri, Locate.At(Source, "jsr gfx::|init"), timeout);
        Assert.Equal(Locate.Span(Source, ".proc |init"), routine?.Range);
    }

    [Fact]
    public async Task ReferencesFindEveryUse()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var all = await client.ReferencesAsync(Uri, Locate.At(Source, ".data |ptr"), includeDeclaration: true, timeout);
        Assert.Equal([2, 10, 19], all.Select(location => location.Range.Start.Line));

        var uses = await client.ReferencesAsync(
            Uri, Locate.At(Source, ".data |ptr"), includeDeclaration: false, timeout);
        Assert.Equal([10, 19], uses.Select(location => location.Range.Start.Line));
    }

    /// <summary>A cheap local is private to its proc, so its uses are the ones inside it.</summary>
    [Fact]
    public async Task ReferencesToACheapLocalStayInItsProc()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var all = await client.ReferencesAsync(Uri, Locate.At(Source, "@loop:"), includeDeclaration: true, timeout);
        Assert.Equal([11, 12], all.Select(location => location.Range.Start.Line));
    }

    [Fact]
    public async Task HighlightsMarkTheDeclarationAsAWrite()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var highlights = await client.HighlightsAsync(Uri, Locate.At(Source, "lda |ptr"), timeout);
        Assert.Equal(
            [DocumentHighlightKind.Write, DocumentHighlightKind.Read, DocumentHighlightKind.Read],
            highlights.Select(highlight => highlight.Kind));
    }

    [Fact]
    public async Task RenameRewritesEveryOccurrence()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // What a rename replaces is the name under the caret, which is what the client shows
        // the programmer to edit.
        Assert.Equal(Locate.Span(Source, "lda |ptr"),
            await client.PrepareRenameAsync(Uri, Locate.At(Source, "lda |ptr"), timeout));

        var edit = await client.RenameAsync(Uri, Locate.At(Source, "lda |ptr"), "pointer", timeout);
        Assert.NotNull(edit);
        var edits = edit.Changes[Uri];
        Assert.Equal([2, 10, 19], edits.Select(e => e.Range.Start.Line));
        Assert.All(edits, e => Assert.Equal("pointer", e.NewText));
    }

    /// <summary>Renaming a cheap local changes only its proc, and the new name must keep the <c>@</c>.</summary>
    [Fact]
    public async Task ACheapLocalIsRenamedWithItsAt()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var edit = await client.RenameAsync(Uri, Locate.At(Source, "@loop:"), "@again", timeout);
        Assert.NotNull(edit);
        Assert.Equal([11, 12], edit.Changes[Uri].Select(e => e.Range.Start.Line));

        var refused = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            client.RenameAsync(Uri, Locate.At(Source, "@loop:"), "again", timeout));
        Assert.Contains("must start with `@`", refused.Message);
    }

    [Theory]
    [InlineData("x", "is a register name")]
    [InlineData("2fast", "is not a name")]
    [InlineData("SCREEN", "is already declared in this scope")]
    public async Task ARenameThatWouldNotCompileIsRefused(string newName, string reason)
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var refused = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            client.RenameAsync(Uri, Locate.At(Source, ".data |ptr"), newName, timeout));
        Assert.Contains(reason, refused.Message);
    }

    /// <summary>Names are resolved as the document is edited, not as it was opened.</summary>
    [Fact]
    public async Task AnEditChangesWhatANameMeans()
    {
        var timeout = TestTimeout.Token();
        const string Text =
            ".module main\n.const COUNT = 1\n.segment CODE\n.export .proc main {\n    lda #COUNT\n    rts\n}\n";
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Text);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        // Rename the declaration alone, and the use no longer resolves.
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(
            Locate.Span(Text, "COUNT"), "TOTAL"));

        var published = await client.NextDiagnosticsAsync(timeout);
        var diagnostic = Assert.Single(published.Diagnostics);
        Assert.Equal("`COUNT` is not declared", diagnostic.Message);
        Assert.Equal(4, diagnostic.Range.Start.Line);

        var edited = Text.Replace(".const COUNT = 1", ".const TOTAL = 1", StringComparison.Ordinal);
        var hover = await client.HoverAsync(Uri, Locate.At(edited, "TOTAL ="), timeout);
        Assert.Contains(".const TOTAL = 1", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A branch the build leaves out is not a problem, so it is published as a hint the
    /// client renders faded rather than as anything that belongs in a problem list.
    /// </summary>
    [Fact]
    public async Task ABranchTheBuildLeavesOutIsPublishedAsFaded()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\n.if 0 {\n.const BROKEN = nowhere\n}\n.const ON = 1\n.export ON\n");

        var dimmed = Assert.Single((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        Assert.Equal(DiagnosticSeverity.Hint, dimmed.Severity);
        Assert.Equal([DiagnosticTag.Unnecessary], dimmed.Tags);
        // The `.if` through its closing brace, and not the line after it.
        Assert.Equal(1, dimmed.Range.Start.Line);
        Assert.Equal(3, dimmed.Range.End.Line);
    }

    /// <summary>A duplicate declaration points at the one that got there first.</summary>
    [Fact]
    public async Task ADuplicateIncludesRelatedInformation()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\n.const SIZE = 1\n.const SIZE = 2\n");

        var diagnostic = Assert.Single((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        Assert.Equal("`SIZE` is already declared in this scope", diagnostic.Message);
        var related = Assert.Single(diagnostic.RelatedInformation!);
        Assert.Equal(1, related.Location.Range.Start.Line);
    }

    /// <summary>A request about a document the client never opened is answered, not refused.</summary>
    [Fact]
    public async Task ADocumentThatIsNotOpenAnswersEmpty()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);

        Assert.Null(await client.HoverAsync(Uri, new Position(1, 0), timeout));
        Assert.Null(await client.DefinitionAsync(Uri, new Position(1, 0), timeout));
        Assert.Empty(await client.ReferencesAsync(Uri, new Position(1, 0), true, timeout));
        Assert.Empty(await client.HighlightsAsync(Uri, new Position(1, 0), timeout));
        Assert.Null(await client.PrepareRenameAsync(Uri, new Position(1, 0), timeout));
        Assert.Null(await client.RenameAsync(Uri, new Position(1, 0), "x", timeout));
    }

    private static Task<TestClient> OpenAsync(CancellationToken cancellation) =>
        TestClient.OpenedCleanlyAsync(cancellation, (Uri, Source));
}
