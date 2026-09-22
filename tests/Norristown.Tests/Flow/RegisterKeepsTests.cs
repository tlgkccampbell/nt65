using Norristown.Flow;
using Norristown.Processor;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Which registers a routine hands back as it was entered with them: what each instruction
/// does, how a save and its restore cancel, what a call takes away, and what a routine that
/// promises more than it keeps is told.
/// </summary>
public sealed class RegisterKeepsTests
{
    /// <summary>A routine that touches nothing hands every register back.</summary>
    [Fact]
    public void ARoutineThatTouchesNothingKeepsEverything()
    {
        Assert.Equal(Registers.All, Kept(".proc p {\n    nop\n    rts\n}\n", "p"));
    }

    /// <summary>What an instruction writes, it does not hand back.</summary>
    [Theory]
    [InlineData("lda #1", Registers.X | Registers.Y | Registers.C)]
    [InlineData("ldx #1", Registers.A | Registers.Y | Registers.C)]
    [InlineData("ldy #1", Registers.A | Registers.X | Registers.C)]
    [InlineData("clc", Registers.A | Registers.X | Registers.Y)]
    [InlineData("adc #1", Registers.X | Registers.Y)]
    [InlineData("cmp #1", Registers.A | Registers.X | Registers.Y)]
    [InlineData("inx", Registers.A | Registers.Y | Registers.C)]
    [InlineData("sta $10", Registers.All)]
    [InlineData("asl $10", Registers.A | Registers.X | Registers.Y)]
    [InlineData("asl a", Registers.X | Registers.Y)]
    public void AnInstructionDoesNotHandBackWhatItWrites(string instruction, Registers kept)
    {
        Assert.Equal(kept, Kept($".proc p {{\n    {instruction}\n    rts\n}}\n", "p"));
    }

    /// <summary>
    /// A save and its restore cancel, because the stack remembers whose value each push holds.
    /// The 6502 saves X through the accumulator, so the value has to be followed across the
    /// transfers as well as the pushes.
    /// </summary>
    [Fact]
    public void ASaveAndItsRestoreCancel()
    {
        Assert.Equal(Registers.All, Kept(".proc p {\n    pha\n    txa\n    pha\n    lda #1\n"
            + "    ldx #2\n    pla\n    tax\n    pla\n    rts\n}\n", "p"));
    }

    /// <summary>A pull that does not match the push above it gets back nothing anyone can name.</summary>
    [Fact]
    public void APullThatDoesNotMatchItsPushRestoresNothing()
    {
        Assert.Equal(
            Registers.Y | Registers.C, Kept(".proc p {\n    pha\n    lda #1\n    ldx #2\n    plx\n    rts\n}\n", "p"));
    }

    /// <summary>A `php` and its `plp` hand back the carry, across a call and a label.</summary>
    [Fact]
    public void APhpAndItsPlpHandBackTheCarry()
    {
        Assert.Equal(Registers.C, Kept(".proc q {\n    clc\n    rts\n}\n"
            + ".proc p {\n    php\n    lda #1\n    ldx #2\n    ldy #3\n    jsr q\n    plp\n    rts\n}\n", "p"));
    }

    /// <summary>A save on one path and none on the other is no save at all.</summary>
    [Fact]
    public void ASaveOnOnlyOnePathIsNoSave()
    {
        Assert.Equal(Registers.X | Registers.Y | Registers.C, Kept(".proc p {\n    beq @skip\n    pha\n    lda #1\n"
            + "    pla\n@skip:\n    lda #2\n    rts\n}\n", "p"));
    }

    /// <summary>A call hands back what the routine it names hands back, and no more.</summary>
    [Fact]
    public void ACallHandsBackWhatItsRoutineHandsBack()
    {
        var text = ".proc q {\n    ldy #1\n    rts\n}\n.proc p {\n    jsr q\n    rts\n}\n";

        Assert.Equal(Registers.A | Registers.X | Registers.C, Kept(text, "q"));
        Assert.Equal(Registers.A | Registers.X | Registers.C, Kept(text, "p"));
    }

    /// <summary>
    /// Two routines that call each other settle, because the set only ever shrinks: this is the
    /// one place the registers are easier than the cycle counts, which come out unknown.
    /// </summary>
    [Fact]
    public void TwoRoutinesThatCallEachOtherSettle()
    {
        var text = ".proc p {\n    ldx #1\n    jsr q\n    rts\n}\n.proc q {\n    ldy #1\n    jsr p\n    rts\n}\n";

        Assert.Equal(Registers.A | Registers.C, Kept(text, "p"));
        Assert.Equal(Registers.A | Registers.C, Kept(text, "q"));
    }

    /// <summary>
    /// A call to an address no declaration stands at is one nt65 cannot follow, and the answer
    /// says so rather than letting silence read as safety.
    /// </summary>
    [Fact]
    public void ACallThatCannotBeFollowedIsSaidToBeUnknown()
    {
        var registers = Found(".proc CHROUT = $FFD2\n.proc p {\n    jsr CHROUT+3\n    rts\n}\n", "p");

        Assert.Equal(Registers.None, registers.Kept);
        Assert.False(registers.Complete);
    }

    /// <summary>
    /// A routine whose body is not here keeps what it declares and nothing else, which is the
    /// only way anyone can know: no body will ever say otherwise.
    /// </summary>
    [Fact]
    public void ARoutineWithNoBodyKeepsWhatItDeclares()
    {
        var text = ".proc CHROUT = $FFD2: keeps x, y\n.proc p {\n    jsr CHROUT\n    rts\n}\n";

        Assert.Equal(Registers.X | Registers.Y, Kept(text, "p"));
    }

    /// <summary>A routine with no body that declares nothing is one nothing is known about.</summary>
    [Fact]
    public void ARoutineWithNoBodyThatDeclaresNothingIsUnknown()
    {
        var registers = Found(".proc CHROUT = $FFD2\n.proc p {\n    jsr CHROUT\n    rts\n}\n", "p");

        Assert.Equal(Registers.None, registers.Kept);
        Assert.False(registers.Complete);
    }

    /// <summary>A body that breaks what its signature promises is told which register and where.</summary>
    [Fact]
    public void ABodyThatBreaksItsPromiseIsTold()
    {
        Assert.Equal(
            ["main.nt65:3: `p` promises `keeps x`, and X is not what the routine was entered with here: "
                + "restore it before returning, or a `.state keeps x` where the value comes back says so"],
            Problems(".proc p: keeps x {\n    ldx #1\n    rts\n}\n"));
    }

    /// <summary>A body that keeps what it promises is told nothing.</summary>
    [Fact]
    public void ABodyThatKeepsItsPromiseIsToldNothing()
    {
        Assert.Empty(Problems(".proc p: keeps x {\n    pha\n    txa\n    pha\n    ldx #1\n    pla\n"
            + "    tax\n    pla\n    rts\n}\n"));
    }

    /// <summary>
    /// A restore through memory is not seen, because ruling out every store that could have
    /// reached the byte would need addresses, which are the linker's. A `.state keeps` says it.
    /// </summary>
    [Fact]
    public void ARestoreThroughMemoryNeedsSaying()
    {
        var saved = ".segment BSS\n.data xsave: .byte\n.segment CODE\n";

        Assert.Equal(
            ["main.nt65:8: `p` promises `keeps x`, and X is not what the routine was entered with here: "
                + "restore it before returning, or a `.state keeps x` where the value comes back says so"],
            Problems(saved + ".proc p: keeps x {\n    stx xsave\n    ldx #1\n    ldx xsave\n    rts\n}\n"));
        Assert.Empty(Problems(
            saved + ".proc p: keeps x {\n    stx xsave\n    ldx #1\n    ldx xsave\n    .state keeps x\n    rts\n}\n"));
    }

    /// <summary>A `.state keeps` where the register was never destroyed is one nobody needed.</summary>
    [Fact]
    public void ARedundantStateKeepsIsSaidToSayNothing()
    {
        Assert.Equal(
            ["main.nt65:3: `keeps x` says nothing here: X is already what the routine was entered with"],
            Problems(".proc p {\n    lda #1\n    .state keeps x\n    rts\n}\n"));
    }

    /// <summary>
    /// An interrupt handler returns to whatever it broke into, which is where handing the
    /// registers back matters most, so `rti` is checked as a return.
    /// </summary>
    [Fact]
    public void AnInterruptHandlerIsCheckedAtItsRti()
    {
        Assert.Equal(
            ["main.nt65:3: `p` promises `keeps a`, and A is not what the routine was entered with here: "
                + "restore it before returning, or a `.state keeps a` where the value comes back says so"],
            Problems(".proc p: interrupt, keeps a {\n    lda #1\n    rti\n}\n"));
    }

    /// <summary>A label another routine may jump into is one nothing arrives at in a known state.</summary>
    [Fact]
    public void ADeclaredLabelIsEnteredWithNothingKnown()
    {
        Assert.Equal(
            Registers.None,
            Kept(".proc p {\n    rts\n@entry:\n    .state\n    rts\n}\n", "p"));
    }

    /// <summary>A tail jump hands this routine's caller whatever the routine it jumps to hands back.</summary>
    [Fact]
    public void ATailJumpTakesWhatItJumpsTo()
    {
        var text = ".proc q {\n    ldy #1\n    rts\n}\n.proc p {\n    jmp q\n}\n";

        Assert.Equal(Registers.A | Registers.X | Registers.C, Kept(text, "p"));
    }

    /// <summary>
    /// A `.state` carrying nothing but `keeps` says what a register holds, not what the
    /// processor state at a label is, so it neither declares the label nor answers what a
    /// routine with no body assumes.
    /// </summary>
    [Fact]
    public void AStateOfNothingButKeepsDeclaresNothing()
    {
        // A `.state` after a label makes it an entry point, which is why nothing running into
        // it is no longer worth reporting. One of nothing but `keeps` leaves it as it was.
        Assert.Empty(Wide(".proc p: a8, i8 {\n    rts\n@entry:\n    .state a8, i8, native\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:3: `@entry` is never reached: nothing runs into it and nothing names it"],
            Wide(".proc p: a8, i8 {\n    rts\n@entry:\n    .state keeps x\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:1: `rom` declares no processor state, and an extern proc has no body to check one "
                + "against: say what a caller must hold to, or `?` where nothing is known"],
            Wide(".proc rom = $FFD2: keeps x\n"));
    }

    /// <summary>
    /// On the 65816 a push is as wide as the register, so a pull gets the value back only where
    /// the width is the same at both.
    /// </summary>
    [Fact]
    public void AWidthThatChangesBetweenAPushAndItsPullBreaksTheSave()
    {
        Assert.Empty(Wide(".proc p: a8, keeps a {\n    pha\n    lda #1\n    pla\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:5: `p` promises `keeps a`, and A is not what the routine was entered with here: "
                + "restore it before returning, or a `.state keeps a` where the value comes back says so"],
            Wide(".proc p: a8, keeps a -> a16 {\n    pha\n    rep #$20\n    pla\n    rts\n}\n"));
    }

    private static Registers Kept(string text, string routine) => Found(text, routine).Kept;

    private static RoutineRegisters Found(string text, string routine)
    {
        var analysis = Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 6502\n.segment CODE\n" + text));
        var flow = analysis.FlowFor("main.nt65");
        Assert.NotNull(flow);
        return flow.Regions.Single(region => region.Routine.DisplayName == routine).Registers;
    }

    private static IReadOnlyList<string> Problems(string text) =>
        [.. Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 6502\n.segment CODE\n" + text)).Problems()
            .Select(Renumbered)];

    /// <summary>The same for a 65816 program, where a push is as wide as the register it moves.</summary>
    private static IReadOnlyList<string> Wide(string text) =>
        [.. Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n" + text)).Problems()
            .Select(Renumbered)];

    /// <summary>A problem's line as the test wrote it, without the three lines every test is given.</summary>
    private static string Renumbered(string problem)
    {
        var parts = problem.Split(':', 3);
        return $"{parts[0]}:{int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) - 3}:{parts[2]}";
    }
}
