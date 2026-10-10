using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the fixes for diagnostics that suggest one, requested from the workspace directly rather
/// than through the protocol. Each fix is applied, the resulting file is compared with what the
/// programmer would have written, and that file is checked to have no diagnostics at all.
/// </summary>
public sealed class FixesTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Header = ".module main\n.cpu 65816\n.segment CODE\n";

    /// <summary>A body long enough that a branch over it cannot reach.</summary>
    private static readonly string Far = string.Concat(Enumerable.Repeat("    nop\n", 130));

    /// <summary>
    /// Gets a range covering the whole file, as a client sends when it asks for actions across all
    /// of it.
    /// </summary>
    private static Range Whole => new(new Position(0, 0), new Position(1000, 0));

    public static TheoryData<string, string, string> Fixes => new()
    {
        {
            "Branch with `jne`",
            $".export .proc main: a8, i8 {{\n    bne @done\n{Far}@done:\n    rts\n}}\n",
            $".export .proc main: a8, i8 {{\n    jne @done\n{Far}@done:\n    rts\n}}\n"
        },
        {
            "Mark the routine `interrupt`",
            ".proc irq: native {\n    rti\n}\n.segment RODATA\n.data vectors: .addr irq\n",
            ".proc irq: native, interrupt {\n    rti\n}\n.segment RODATA\n.data vectors: .addr irq\n"
        },
        {
            "Mark the routine `interrupt`",
            ".proc irq {\n    rti\n}\n.segment RODATA\n.data vectors: .addr irq\n",
            ".proc irq: interrupt {\n    rti\n}\n.segment RODATA\n.data vectors: .addr irq\n"
        },
        {
            "Leave with `rti`",
            ".proc irq: interrupt {\n    rts\n}\n",
            ".proc irq: interrupt {\n    rti\n}\n"
        },
        {
            "Change to `.strz`",
            ".data title: .asciiz \"hi\"\n",
            ".data title: .strz \"hi\"\n"
        },
        {
            "Change it to `counter`",
            ".data counter: .byte 0\n.export .proc main: a8, i8 {\n    lda countr\n    rts\n}\n",
            ".data counter: .byte 0\n.export .proc main: a8, i8 {\n    lda counter\n    rts\n}\n"
        },
        {
            "Drop the level: an assertion that fails is an error",
            ".assert 1 == 1, error, \"always\"\n",
            ".assert 1 == 1, \"always\"\n"
        },
        {
            "Declare it as `.byte[4]`",
            ".data buffer: .res 4\n.export .proc main: a8, i8 {\n    lda buffer\n    rts\n}\n",
            ".data buffer: .byte[4]\n.export .proc main: a8, i8 {\n    lda buffer\n    rts\n}\n"
        },
        {
            "Make `last` a member of the data",
            ".data table {\n    .word 1\nlast: .word 2\n}\n",
            ".data table {\n    .word 1\n.data last: .word 2\n}\n"
        },
        {
            "Make `here` a position, `@here`",
            ".data table {\n    .word here - table\nhere:\n    .word 2\n}\n",
            ".data table {\n    .word @here - table\n@here:\n    .word 2\n}\n"
        },
        {
            "Export it as `abs`",
            ".export marker: zp\n.data marker: .word 0\n",
            ".export marker: abs\n.data marker: .word 0\n"
        },
        {
            "Change to `(1 & 2) == 0`",
            ".const MASK = 1 & 2 == 0\n.export .proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n",
            ".const MASK = (1 & 2) == 0\n.export .proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n"
        },
        {
            "Change to `1 & (2 == 0)`",
            ".const MASK = 1 & 2 == 0\n.export .proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n",
            ".const MASK = 1 & (2 == 0)\n.export .proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n"
        },
        {
            "Declare it `a8`, which is what the routine assumes",
            ".export .proc main {\n    lda #1\n    rts\n}\n",
            ".export .proc main: a8 {\n    lda #1\n    rts\n}\n"
        },
        {
            "Remove `SPARE`",
            ".const SPARE = 1\n",
            ""
        },
        {
            "Make it the number `#10`",
            ".export .proc main: a8, i8 {\n    lda 10\n    rts\n}\n",
            ".export .proc main: a8, i8 {\n    lda #10\n    rts\n}\n"
        },
        {
            "Write the address as `$0a`",
            ".export .proc main: a8, i8 {\n    lda 10\n    rts\n}\n",
            ".export .proc main: a8, i8 {\n    lda $0a\n    rts\n}\n"
        },
        {
            "Remove it",
            ".export .proc main: a8, i8, native {\n    sep #$20\n    lda #1\n    rts\n}\n",
            ".export .proc main: a8, i8, native {\n    lda #1\n    rts\n}\n"
        },
        {
            "Change it to `#$20`",
            ".export .proc main: a8, i16, native {\n    rep #$30\n    lda #$1234\n    sep #$20\n    rts\n}\n",
            ".export .proc main: a8, i16, native {\n    rep #$20\n    lda #$1234\n    sep #$20\n    rts\n}\n"
        },
        {
            "Jump with `jmp` as a tail call",
            ".proc helper {\n    rts\n}\n.export .proc main: native {\n    jsr helper\n    rts\n}\n",
            ".proc helper {\n    rts\n}\n.export .proc main: native {\n    jmp helper\n}\n"
        },
        {
            "Add `c` to `reads`",
            ".export .proc main: a8, i8, reads a {\n    adc #1\n    sta $10\n    rts\n}\n",
            ".export .proc main: a8, i8, reads a, c {\n    adc #1\n    sta $10\n    rts\n}\n"
        },
        {
            "Add `x` to `reads`",
            ".export .proc main: a8, i8, reads none {\n    stx $10\n    rts\n}\n",
            ".export .proc main: a8, i8, reads x {\n    stx $10\n    rts\n}\n"
        },
        {
            "Jump with `jml` as a tail call",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: far, native {\n    jsl helper\n    rtl\n}\n",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: far, native {\n    jml helper\n}\n"
        },
        {
            "Leave with `rtl`",
            ".export .proc main: far {\n    rts\n}\n",
            ".export .proc main: far {\n    rtl\n}\n"
        },
        {
            "Jump with `jml`",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: far, native {\n    jmp helper\n}\n",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: far, native {\n    jml helper\n}\n"
        },
        {
            "Jump with `jmp`",
            ".proc helper {\n    rts\n}\n.export .proc main: native {\n    jml helper\n}\n",
            ".proc helper {\n    rts\n}\n.export .proc main: native {\n    jmp helper\n}\n"
        },
        {
            "Push the bank with `phk`",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: native {\n    per back-1\n    brl helper\nback:\n    rts\n}\n",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: native {\n    phk\n    per back-1\n    brl helper\nback:\n    rts\n}\n"
        },
        {
            "Remove the `phk`",
            ".proc helper {\n    rts\n}\n.export .proc main: native {\n    phk\n    per back-1\n    brl helper\nback:\n    rts\n}\n",
            ".proc helper {\n    rts\n}\n.export .proc main: native {\n    per back-1\n    brl helper\nback:\n    rts\n}\n"
        },
        {
            "Remove `keeps x`",
            ".export .proc main: keeps x {\n    .state keeps x\n    rts\n}\n",
            ".export .proc main: keeps x {\n    rts\n}\n"
        },
        {
            "Remove `x` from `keeps x, y`",
            ".export .proc main: i8, keeps x, y {\n    ldy #0\n    .state keeps x, y\n    rts\n}\n",
            ".export .proc main: i8, keeps x, y {\n    ldy #0\n    .state keeps y\n    rts\n}\n"
        },
        {
            "Remove it",
            ".next ?\n.export .proc main: native {\n    rts\n}\n",
            ".export .proc main: native {\n    rts\n}\n"
        },
        {
            "Remove `far`",
            ".export .proc main: near, far {\n    rts\n}\n",
            ".export .proc main: near {\n    rts\n}\n"
        },
        {
            "Move `keeps x` before `->`",
            ".export .proc main: a8, native -> a16, keeps x {\n    rep #$20\n    rts\n}\n",
            ".export .proc main: a8, native, keeps x -> a16 {\n    rep #$20\n    rts\n}\n"
        },
        {
            "Move `keeps x` before `->`",
            ".export .proc main: a8, native -> keeps x {\n    rts\n}\n",
            ".export .proc main: a8, native, keeps x {\n    rts\n}\n"
        },
        {
            "Remove the block and keep its contents",
            ".export .proc main {\n    nop\n    .segment CODE {\n        nop\n    }\n    rts\n}\n",
            ".export .proc main {\n    nop\n    nop\n    rts\n}\n"
        },
        {
            "Branch with `beq` around a `jml helper`",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: far, native {\n    bne helper\n    rtl\n}\n",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: far, native {\n    beq @skip\n    jml helper\n@skip:\n    rtl\n}\n"
        },
        {
            "Jump with `jml`",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: far, native {\n    bra helper\n}\n",
            ".proc helper: far {\n    rtl\n}\n.export .proc main: far, native {\n    jml helper\n}\n"
        },
        {
            "Add `.ensure a16`",
            ".proc helper: a16, i8 -> a8 {\n    sep #$20\n    rts\n}\n.export .proc main: a8, i8, native {\n    jsr helper\n    rts\n}\n",
            ".proc helper: a16, i8 -> a8 {\n    sep #$20\n    rts\n}\n.export .proc main: a8, i8, native {\n    .ensure a16\n    jsr helper\n    rts\n}\n"
        },
        {
            "Add `.ensure a16`",
            ".macro wide(): a16 -> a8 {\n    lda #$1234\n    sep #$20\n}\n.export .proc main: a8, i8, native {\n    wide!()\n    rts\n}\n",
            ".macro wide(): a16 -> a8 {\n    lda #$1234\n    sep #$20\n}\n.export .proc main: a8, i8, native {\n    .ensure a16\n    wide!()\n    rts\n}\n"
        },
        {
            "Add `.ensure a8`",
            ".export .proc main: a8, i8, native {\n    rep #$20\n    rts\n}\n",
            ".export .proc main: a8, i8, native {\n    rep #$20\n    .ensure a8\n    rts\n}\n"
        },
        {
            "Declare that `main` returns with `a16`",
            ".export .proc main: a8, i8, native {\n    rep #$20\n    rts\n}\n",
            ".export .proc main: a8, i8, native -> a16 {\n    rep #$20\n    rts\n}\n"
        },
        {
            "Declare that `main` returns with `a16`",
            ".export .proc main: a8, i8, native -> i8 {\n    rep #$20\n    rts\n}\n",
            ".export .proc main: a8, i8, native -> i8, a16 {\n    rep #$20\n    rts\n}\n"
        },
        {
            "Declare that `main` returns with `emu`",
            ".export .proc main: a8, i8, native -> native {\n    sec\n    xce\n    rts\n}\n",
            ".export .proc main: a8, i8, native -> emu {\n    sec\n    xce\n    rts\n}\n"
        },
        {
            "Change it to `a8`",
            ".export .proc main: a8, i8 {\n    .state a16, i8, native\n    rts\n}\n",
            ".export .proc main: a8, i8 {\n    .state a8, i8, native\n    rts\n}\n"
        },
        {
            "Change it to `native`",
            ".export .proc main: a8, i8, native {\n    .state a8, i8, emu\n    rts\n}\n",
            ".export .proc main: a8, i8, native {\n    .state a8, i8, native\n    rts\n}\n"
        },
        {
            "Change it to `dp = $0000`",
            ".export .proc main: a8, i8, dp = $0000 {\n    .state a8, i8, native, dp = $2100\n    rts\n}\n",
            ".export .proc main: a8, i8, dp = $0000 {\n    .state a8, i8, native, dp = $0000\n    rts\n}\n"
        },
        {
            "Allow `unused-symbol` here with `.allow`",
            ".proc helper {\n    rts\n}\n",
            ".allow \"unused-symbol\"\n.proc helper {\n    rts\n}\n"
        },
        {
            "Remove it",
            ".export helper\n.allow \"unused-symbol\"\n.proc helper {\n    rts\n}\n",
            ".export helper\n.proc helper {\n    rts\n}\n"
        },
        {
            "List the variant with `as ora`",
            ".export .proc main: a8, i8 {\n    lda #.opcode(ora, imm)\n    sta @op\n    .patch @op\n@op:\n    and #1\n    rts\n}\n",
            ".export .proc main: a8, i8 {\n    lda #.opcode(ora, imm)\n    sta @op\n    .patch @op as ora\n@op:\n    and #1\n    rts\n}\n"
        },
        {
            "Name `@next` in the `.patch` instead",
            ".export .proc main: a16, i8 {\n    sta @op+3\n    .patch @op\n@op:\n    ldx #0\n@next:\n    lda #0\n    rts\n}\n",
            ".export .proc main: a16, i8 {\n    sta @op+3\n    .patch @next\n@op:\n    ldx #0\n@next:\n    lda #0\n    rts\n}\n"
        },
        {
            "Remove it",
            ".export .proc main: a16, i8 {\n    sta @op+3\n    .patch @op\n    .patch @next\n@op:\n    ldx #0\n@next:\n    lda #0\n    rts\n}\n",
            ".export .proc main: a16, i8 {\n    sta @op+3\n    .patch @next\n@op:\n    ldx #0\n@next:\n    lda #0\n    rts\n}\n"
        },
    };

    [Theory]
    [MemberData(nameof(Fixes))]
    public void AFixAppliesWhatTheDiagnosticNames(string title, string body, string fixedBody)
    {
        var (analysis, model) = Analyzed(Header + body);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Title == title);

        Assert.Equal("quickfix", action.Kind);
        Assert.Equal([Uri], action.Edit!.Changes.Keys);
        Assert.Equal(Header + fixedBody, Editing.Apply(Header + body, action.Edit!.Changes[Uri]));

        // What the fix leaves is a file with nothing wrong.
        var (after, _) = Analyzed(Header + fixedBody);
        Assert.Empty(after.Diagnostics.Select(diagnostic => diagnostic.Message));
    }

    /// <summary>
    /// The fix that exports an unused name inserts the <c>.export</c> directly under the file's
    /// <c>.module</c>, which is also where the fix for a name another module cannot see inserts
    /// one.
    /// </summary>
    [Fact]
    public void ExportingWhatNothingNamesInsertsTheExportUnderTheModule()
    {
        var (analysis, model) = Analyzed(Header + ".const SPARE = 1\n");

        var action = Assert.Single(CodeActions.In(analysis, model, Whole),
            action => action.Title == "Export `SPARE` from `main`");

        Assert.Equal(
            ".module main\n.export SPARE\n.cpu 65816\n.segment CODE\n.const SPARE = 1\n",
            Editing.Apply(Header + ".const SPARE = 1\n", action.Edit!.Changes[Uri]));
    }

    /// <summary>
    /// A declaration in a repetition's body is a different one on every iteration and no path
    /// reaches it, so the message does not mention exporting it, and the fix that removes it is
    /// the only one offered for it.
    /// </summary>
    [Fact]
    public void WhatARepetitionDeclaresIsOfferedRemovalAndNotExport()
    {
        const string Body = ".segment BSS\n.repeat 2 {\n    .data spare: .byte\n}\n";
        var (analysis, model) = Analyzed(Header + Body);

        var unused = Assert.Single(analysis.Diagnostics, diagnostic => diagnostic.Id == "unused-symbol");
        var titles = CodeActions.In(analysis, model, Whole).Select(action => action.Title).ToList();

        Assert.Equal("`spare` is never used", unused.Message);
        Assert.Contains("Remove `spare`", titles);
        Assert.DoesNotContain(titles, title => title.StartsWith("Export", StringComparison.Ordinal));
        Assert.Equal(Header + ".segment BSS\n.repeat 2 {\n}\n",
            Editing.Apply(Header + Body, CodeActions.In(analysis, model, Whole).Single(action => action.Title == "Remove `spare`").Edit!.Changes[Uri]));
    }

    /// <summary>
    /// Where the analysis cannot work out a register width, only the programmer can say which it
    /// is, so both widths are offered and neither is marked preferred, since neither should be
    /// applied without asking.
    /// </summary>
    [Fact]
    public void AWidthThatIsNotKnownOffersEitherOne()
    {
        const string Body = ".proc other: a8, i8 -> ? {\n    rts\n}\n.export .proc main: a8, i8 {\n    jsr other\n    lda #1\n    rts\n}\n";
        var (analysis, model) = Analyzed(Header + Body);

        var actions = CodeActions.In(analysis, model, Whole)
            .Where(action => action.Title.StartsWith("Add `.ensure", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            ["Add `.ensure a8`", "Add `.ensure a16`"],
            actions.Select(action => action.Title));
        Assert.All(actions, action => Assert.False(action.IsPreferred));
        Assert.Equal(
            Header + ".proc other: a8, i8 -> ? {\n    rts\n}\n.export .proc main: a8, i8 {\n    jsr other\n    .ensure a8\n    lda #1\n    rts\n}\n",
            Editing.Apply(Header + Body, actions[0].Edit!.Changes[Uri]));
    }

    /// <summary>
    /// A label entered from where nt65 cannot see, here through its address, gets a
    /// <c>.state</c> only as a choice. Declaring the state the visible path brings asserts it of
    /// the unseen entrants too, so that fix says where its state came from and is not preferred.
    /// <c>.state ?</c>, which assumes nothing, is offered beside it.
    /// </summary>
    [Fact]
    public void AnEntryNt65CannotSeeOffersTheInferredStateAndUnknown()
    {
        const string Body = ".export .proc main: a8, i8, native {\n    lda #<@here\n@here:\n    rts\n}\n";
        var (analysis, model) = Analyzed(Header + Body);

        var actions = CodeActions.In(analysis, model, Whole)
            .Where(action => action.Title.StartsWith("Declare `@here`", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            ["Declare `@here` with the state the visible paths bring (`.state a8, i8, native`)", "Declare `@here` with `.state ?`"],
            actions.Select(action => action.Title));
        Assert.All(actions, action => Assert.False(action.IsPreferred));
        Assert.Equal(
            Header + ".export .proc main: a8, i8, native {\n    lda #<@here\n@here:\n    .state ?\n    rts\n}\n",
            Editing.Apply(Header + Body, actions[1].Edit!.Changes[Uri]));
    }

    /// <summary>
    /// A call into a label inside a routine that runs in native mode is offered a <c>.state</c>
    /// that declares the mode as well as the widths, whether the state comes from the path above
    /// the label or, where nothing reaches it from above, from the routine's entry. Declaring it
    /// leaves the code after the label knowing the mode, so nothing there is reported.
    /// </summary>
    [Theory]
    [InlineData("    rts\n", "its routine's entry state")]
    [InlineData("    sep #$30\n", "the state the visible paths bring")]
    public void ACallIntoALabelOffersAStateWithTheMode(string above, string source)
    {
        var body = ".proc owner: a8, i8, native {\n" + above + "inner:\n    rep #$20\n    lda #$1234\n    sep #$20\n    rts\n}\n"
            + ".export .proc main: a8, i8, native {\n    jsr owner::inner\n    rts\n}\n";
        var (analysis, model) = Analyzed(Header + body);

        var actions = CodeActions.In(analysis, model, Whole)
            .Where(action => action.Title.StartsWith("Declare `inner`", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [$"Declare `inner` with {source} (`.state a8, i8, native`)", "Declare `inner` with `.state ?`"],
            actions.Select(action => action.Title));
        var (fixedAnalysis, _) = Analyzed(Editing.Apply(Header + body, actions[0].Edit!.Changes[Uri]));
        Assert.DoesNotContain(fixedAnalysis.Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
    }

    /// <summary>
    /// A return that leaves a width other than the one its routine declares has two readings. The
    /// routine may need to set the width it declares, or it may declare the wrong one. Both fixes
    /// are offered, and neither is preferred.
    /// </summary>
    [Fact]
    public void AReturnInTheWrongStateOffersBothReadings()
    {
        var (analysis, model) = Analyzed(Header + ".export .proc main: a8, i8, native {\n    rep #$20\n    rts\n}\n");

        var actions = CodeActions.In(analysis, model, Whole).Where(action => action.Kind == "quickfix").ToList();

        Assert.Equal(
            ["Add `.ensure a8`", "Declare that `main` returns with `a16`"],
            actions.Select(action => action.Title));
        Assert.All(actions, action => Assert.False(action.IsPreferred));
    }

    /// <summary>
    /// A store into an opcode is offered a variant only where the immediate load of the stored
    /// register before it shows which instruction it writes. A value nt65 cannot see, a register
    /// set by anything other than an immediate load, or an opcode in another addressing mode gives
    /// no variant to list.
    /// </summary>
    [Theory]
    [InlineData("    lda $10\n    sta @op\n")]
    [InlineData("    lda #.opcode(ora, abs)\n    sta @op\n")]
    [InlineData("    lda #.opcode(ora, imm)\n    tax\n    lda $10\n    stx @op\n")]
    public void AStoreWhoseOpcodeIsNotSeenIsOfferedNoVariant(string store)
    {
        var (analysis, model) = Analyzed(Header
            + $".export .proc main: a8, i8 {{\n{store}    .patch @op\n@op:\n    and #1\n    rts\n}}\n");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Id == "patch-variants-required");
        Assert.DoesNotContain(CodeActions.In(analysis, model, Whole), action => action.Title.StartsWith("List the variant", StringComparison.Ordinal));
    }

    /// <summary>
    /// A store that writes the operand of one instruction and the opcode of the labeled one after
    /// it writes both, so the fix adds a <c>.patch</c> for the second rather than renaming the first.
    /// </summary>
    [Fact]
    public void AStoreIntoTwoInstructionsIsOfferedASecondPatch()
    {
        const string Body = ".export .proc main: a16, i8 {\n    sta @op+1\n    .patch @op\n@op:\n    ldx #0\n@next:\n    inx\n    rts\n}\n";
        var (analysis, model) = Analyzed(Header + Body);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Title == "Add `.patch @next`");

        Assert.Equal(
            Header + ".export .proc main: a16, i8 {\n    sta @op+1\n    .patch @op\n    .patch @next\n@op:\n    ldx #0\n@next:\n    inx\n    rts\n}\n",
            Editing.Apply(Header + Body, action.Edit!.Changes[Uri]));
        Assert.DoesNotContain(CodeActions.In(analysis, model, Whole), action => action.Title.Contains("instead", StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks that a store whose bytes land where the <c>.patch</c> cannot name them is offered
    /// no <c>.patch</c> target. A label inside a macro's expansion is out of the routine's reach,
    /// and the first instruction of the next routine is named by that routine's name.
    /// </summary>
    /// <param name="body">The program after the header.</param>
    [Theory]
    [InlineData(".macro load() {\n@op:\n    lda #0\n}\n.export .proc main: a8, i8 {\n    sta @op+2\n    .patch @op\n@op:\n    lda #0\n    load!()\n    rts\n}\n")]
    [InlineData(".export .proc main: a8, i8 {\n    sta @op+3\n    .patch @op\n@op:\n    lda #0\n    rts\n}\n.export .proc next: a8, i8 {\n    lda #1\n    rts\n}\n")]
    public void AStoreIntoCodeThePatchCannotNameIsOfferedNoTarget(string body)
    {
        var (analysis, model) = Analyzed(Header + body);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Id == "patch-misses-store");
        Assert.DoesNotContain(CodeActions.In(analysis, model, Whole), action => action.Title.Contains("`.patch", StringComparison.Ordinal));
    }

    /// <summary>
    /// An <c>.ensure</c> before a conditional branch would set the width on the path that falls
    /// through as well, so a branch to a routine that needs another width is offered no
    /// <c>.ensure</c>.
    /// </summary>
    [Fact]
    public void ABranchIsOfferedNoEnsure()
    {
        var (analysis, model) = Analyzed(Header
            + ".proc helper: a16, i8 -> a8 {\n    sep #$20\n    rts\n}\n.export .proc main: a8, i8 {\n    beq helper\n    rts\n}\n");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Id == "call-state-mismatch");
        Assert.DoesNotContain(CodeActions.In(analysis, model, Whole), action => action.Title.StartsWith("Add `.ensure", StringComparison.Ordinal));
    }

    /// <summary>
    /// A name brought in by <c>.use</c> and never used is shown faded, and a fix removes it from
    /// the <c>.use</c>.
    /// </summary>
    [Fact]
    public void AUseItemNothingNamesIsOfferedForRemoval()
    {
        const string Gfx = ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n.export .proc fill {\n    rts\n}\n";
        const string Main = ".module main\n.use gfx::{clear, fill}\n.segment CODE\n.export .proc main {\n    jsr clear\n    clc\n    rts\n}\n";
        var document = AnalyzedDocument.Of(("file:///c:/work/gfx.nt65", Gfx), (Uri, Main));

        var brought = Assert.Single(document.Analysis.DiagnosticsFor(document.Path));
        Assert.Equal("`fill` is brought in by `.use` and never used", brought.Message);
        Assert.True(brought.IsUnnecessary);

        var action = Assert.Single(CodeActions.In(document.Analysis, document.Model, Whole), IsAFix);
        Assert.Equal("Remove the `.use` of `fill`", action.Title);
        Assert.Equal(
            ".module main\n.use gfx::clear\n.segment CODE\n.export .proc main {\n    jsr clear\n    clc\n    rts\n}\n",
            Editing.Apply(Main, action.Edit!.Changes[Uri]));
    }

    /// <summary>
    /// A name a <c>.use</c> lists in an item block and nothing names is removed from its line, and
    /// a line or a block left with nothing to list goes with it.
    /// </summary>
    [Theory]
    [InlineData(".use gfx::{\n    clear, fill\n}\n", ".use gfx::{\n    clear\n}\n")]
    [InlineData(".use gfx::{\n    clear\n    fill  ; spare\n}\n", ".use gfx::{\n    clear\n}\n")]
    [InlineData(".use gfx::{\n    fill\n}\n.use gfx::clear\n", ".use gfx::clear\n")]
    public void AUseItemInABlockNothingNamesIsOfferedForRemoval(string uses, string kept)
    {
        const string Gfx = ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n.export .proc fill {\n    rts\n}\n";
        const string Rest = ".segment CODE\n.export .proc main {\n    jsr clear\n    clc\n    rts\n}\n";
        var main = ".module main\n" + uses + Rest;
        var document = AnalyzedDocument.Of(("file:///c:/work/gfx.nt65", Gfx), (Uri, main));

        var brought = Assert.Single(document.Analysis.DiagnosticsFor(document.Path));
        Assert.Equal("`fill` is brought in by `.use` and never used", brought.Message);

        var action = Assert.Single(CodeActions.In(document.Analysis, document.Model, Whole), IsAFix);
        Assert.Equal(".module main\n" + kept + Rest, Editing.Apply(main, action.Edit!.Changes[Uri]));
    }

    /// <summary>
    /// A missing bracket is inserted where the syntax tree holds a place for the missing token.
    /// The position comes from that token rather than from the end of the text, so on a line with
    /// a trailing comment the brace is inserted before the comment rather than after it.
    /// </summary>
    [Theory]
    [InlineData(".export .proc main: a8, i8\n    rts\n}\n", "Insert the missing `{`", ".export .proc main: a8, i8 {\n    rts\n}\n")]
    [InlineData(".export .proc main   ; note\n}\n", "Insert the missing `{`", ".export .proc main {   ; note\n}\n")]
    [InlineData(".const MASK = (1 + 2\n", "Insert the missing `)`", ".const MASK = (1 + 2)\n")]
    [InlineData(".export .proc main: a8, i8 {\n    lda [dp\n    rts\n}\n", "Insert the missing `]`",
        ".export .proc main: a8, i8 {\n    lda [dp]\n    rts\n}\n")]
    [InlineData(".use gfx::{clear\n", "Insert the missing `}`", ".use gfx::{clear}\n")]
    public void AMissingBracketIsInsertedWhereTheTreeHoldsItsPlace(string body, string title, string repaired)
    {
        var (analysis, model) = Analyzed(Header + body);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Title == title);

        Assert.Equal("quickfix", action.Kind);
        Assert.Equal(Header + repaired, Editing.Apply(Header + body, action.Edit!.Changes[Uri]));
    }

    /// <summary>
    /// Inserting the missing brace of a routine's body leaves a file with no diagnostics, which
    /// parses and means what it appears to mean.
    /// </summary>
    [Fact]
    public void InsertingTheMissingBraceLeavesAFileWithNothingWrong()
    {
        const string Body = ".export .proc main: a8, i8\n    rts\n}\n";
        var (analysis, model) = Analyzed(Header + Body);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Title == "Insert the missing `{`");

        var repaired = Editing.Apply(Header + Body, action.Edit!.Changes[Uri]);
        var (after, _) = Analyzed(repaired);
        Assert.Empty(after.Diagnostics.Select(diagnostic => diagnostic.Message));
    }

    /// <summary>
    /// A name that ca65 would read as an instruction has no mechanical fix, so the fix inserts no
    /// text. Instead it puts the caret on the name and starts a rename, because what the name
    /// should be is for the programmer to decide, not for the server to guess.
    /// </summary>
    [Fact]
    public void AMnemonicNameOffersARenameAndEditsNothing()
    {
        var (analysis, model) = Analyzed(Header + ".export .proc main: a8, i8 {\nlda:\n    bra lda\n}\n");

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), IsAFix);

        Assert.Equal("Rename `lda`…", action.Title);
        Assert.Empty(action.Edit!.Changes);
        var rename = Assert.IsType<Command>(action.Command);
        Assert.Equal("nt65.rename", rename.Name);
        Assert.Equal([Uri, 4, 0], rename.Arguments);
    }

    /// <summary>A client that asks for one kind of change is given that kind and no other.</summary>
    [Fact]
    public void OnlyTheKindsAskedForAreOffered()
    {
        var (analysis, model) = Analyzed(Header + ".export .proc main: a8, i8 {\n    jmp ($1234)\n}\n");

        Assert.DoesNotContain(CodeActions.In(analysis, model, Whole, ["refactor"]), action => action.Kind == "quickfix");
        Assert.NotEmpty(CodeActions.In(analysis, model, Whole, ["quickfix"]));
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="action"/> is a quick fix other than the
    /// one that allows a warning, which every warning offers.
    /// </summary>
    private static bool IsAFix(CodeAction action) =>
        action.Kind == "quickfix" && !action.Title.StartsWith("Allow `", StringComparison.Ordinal);

    private static (ProgramAnalysis Analysis, SemanticModel Model) Analyzed(string text)
    {
        var document = AnalyzedDocument.Of((Uri, text));
        return (document.Analysis, document.Model);
    }
}
