using Norristown.Flow;
using Norristown.Processor;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks which registers a routine returns still holding the values it was entered with. The
/// tests cover what each instruction changes, how a save and its restore cancel, and what a call
/// to another routine loses. They also cover the diagnostic for a routine whose body does not
/// keep what its signature promises.
/// </summary>
public sealed class RegisterKeepsTests
{
    /// <summary>A routine that touches nothing hands every register back.</summary>
    [Fact]
    public void ARoutineThatTouchesNothingKeepsEverything()
    {
        Assert.Equal(Registers.All, Kept(".proc p {\n    nop\n    rts\n}\n", "p"));
    }

    /// <summary>A register (or the carry) that an instruction writes is not kept.</summary>
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

    /// <summary>
    /// A pull into a different register from the one pushed restores nothing: <c>plx</c> after
    /// <c>pha</c> leaves X holding A's entry value, which is neither register's own.
    /// </summary>
    [Fact]
    public void APullThatDoesNotMatchItsPushRestoresNothing()
    {
        Assert.Equal(
            Registers.Y | Registers.C, Kept(".proc p {\n    pha\n    lda #1\n    ldx #2\n    plx\n    rts\n}\n", "p"));
    }

    /// <summary>
    /// A `php` and its `plp` hand back the carry, even across a call to a routine that
    /// changes it.
    /// </summary>
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
    /// The kept sets of two routines that call each other reach a fixed point, because the
    /// analysis only ever removes registers from a set. Here registers are easier than cycle
    /// counts, which come out unknown for such routines.
    /// </summary>
    [Fact]
    public void TwoRoutinesThatCallEachOtherConverge()
    {
        var text = ".proc p {\n    ldx #1\n    jsr q\n    rts\n}\n.proc q {\n    ldy #1\n    jsr p\n    rts\n}\n";

        Assert.Equal(Registers.A | Registers.C, Kept(text, "p"));
        Assert.Equal(Registers.A | Registers.C, Kept(text, "q"));
    }

    /// <summary>
    /// nt65 cannot follow a call to an address where no routine is declared, and the result is
    /// marked incomplete, so that the absence of a complaint is not mistaken for safety.
    /// </summary>
    [Fact]
    public void ACallThatCannotBeFollowedIsMarkedUnknown()
    {
        var registers = Found(".proc CHROUT = $FFD2\n.proc p {\n    jsr CHROUT+3\n    rts\n}\n", "p");

        Assert.Equal(Registers.None, registers.Kept);
        Assert.False(registers.Complete);
    }

    /// <summary>
    /// A routine whose body is not in the program keeps exactly what its signature declares:
    /// with no body to analyse, the declaration is the only source of that fact.
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

    /// <summary>
    /// A body that breaks what its signature promises gets a diagnostic that reports which
    /// register and where.
    /// </summary>
    [Fact]
    public void ARoutineThatChangesAKeptRegisterIsReported()
    {
        Assert.Equal(
            ["main.nt65:3: `p` promises `keeps x`, but X is not the same as on entry here: "
                + "restore it before returning, or add `.state keeps x` at the point where the entry value is restored"],
            Problems(".proc p: keeps x {\n    ldx #1\n    rts\n}\n"));
    }

    /// <summary>A body that keeps what it promises draws no diagnostic.</summary>
    [Fact]
    public void ARoutineThatRestoresAKeptRegisterIsNotReported()
    {
        Assert.Empty(Problems(".proc p: keeps x {\n    pha\n    txa\n    pha\n    ldx #1\n    pla\n"
            + "    tax\n    pla\n    rts\n}\n"));
    }

    /// <summary>
    /// A restore through memory is not recognised, because ruling out every other store that
    /// could have reached the saved byte would need addresses, which only the linker assigns.
    /// A `.state keeps` asserts the restore instead.
    /// </summary>
    [Fact]
    public void ARestoreThroughMemoryNeedsAStateKeeps()
    {
        var saved = ".segment BSS\n.data xsave: .byte\n.segment CODE\n";

        Assert.Equal(
            ["main.nt65:8: `p` promises `keeps x`, but X is not the same as on entry here: "
                + "restore it before returning, or add `.state keeps x` at the point where the entry value is restored"],
            Problems(saved + ".proc p: keeps x {\n    stx xsave\n    ldx #1\n    ldx xsave\n    rts\n}\n"));
        Assert.Empty(Problems(
            saved + ".proc p: keeps x {\n    stx xsave\n    ldx #1\n    ldx xsave\n    .state keeps x\n    rts\n}\n"));
    }

    /// <summary>
    /// A `.state keeps` for a register that already holds its entry value is reported as
    /// redundant.
    /// </summary>
    [Fact]
    public void ARedundantStateKeepsIsReported()
    {
        Assert.Equal(
            ["main.nt65:3: `keeps x` is redundant here: X already holds its value from entry"],
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
            ["main.nt65:3: `p` promises `keeps a`, but A is not the same as on entry here: "
                + "restore it before returning, or add `.state keeps a` at the point where the entry value is restored"],
            Problems(".proc p: interrupt, keeps a {\n    lda #1\n    rti\n}\n"));
    }

    /// <summary>
    /// A label that a `.state` declares as an entry point may be jumped to from anywhere, so
    /// nothing is known about the registers there and none is kept.
    /// </summary>
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
    /// A branch to another routine does the same on the path where it is taken, and so does a
    /// `.next` that names one: a path that passes control to another routine leaves this one.
    /// </summary>
    [Fact]
    public void ABranchToARoutineTakesWhatItBranchesTo()
    {
        var branch = ".proc q {\n    ldy #1\n    rts\n}\n.proc p {\n    ldx #0\n    bne q\n    rts\n}\n";
        var next = ".proc q {\n    ldy #1\n    rts\n}\n.proc p {\n    jmp (slot)\n    .next q\n}\n"
            + ".segment BSS\n.data slot: .addr\n";

        Assert.Equal(Registers.A | Registers.C, Kept(branch, "p"));
        Assert.Equal(Registers.A | Registers.X | Registers.C, Kept(next, "p"));
    }

    /// <summary>
    /// A `.state` carrying nothing but `keeps` says what a register holds, not what the
    /// processor state at a label is, so it neither declares the label nor answers what a
    /// routine with no body assumes.
    /// </summary>
    [Fact]
    public void AStateOfNothingButKeepsDeclaresNothing()
    {
        // A `.state` after a label makes it an entry point, so the label is not reported as
        // never reached. A `.state` holding only `keeps` does not, and the label is reported.
        Assert.Empty(Wide(".proc p: a8, i8 {\n    rts\n@entry:\n    .state a8, i8, native\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:3: `@entry` is never reached: no code falls into it and nothing refers to it"],
            Wide(".proc p: a8, i8 {\n    rts\n@entry:\n    .state keeps x\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:1: `rom` is an extern proc with no processor-state signature: nt65 cannot see its body, "
                + "so declare what it expects and leaves, or `?` if that is unknown"],
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
            ["main.nt65:5: `p` promises `keeps a`, but A is not the same as on entry here: "
                + "restore it before returning, or add `.state keeps a` at the point where the entry value is restored"],
            Wide(".proc p: a8, keeps a -> a16 {\n    pha\n    rep #$20\n    pla\n    rts\n}\n"));
    }

    /// <summary>
    /// A scope that something branches into past its start has no single pass through it, so it
    /// gets neither a cost nor a register answer. Here a pass from the top restores A, but a path
    /// that enters at <c>mid</c> pulls a byte the scope never pushed.
    /// </summary>
    [Fact]
    public void AScopeBranchedIntoPastItsStartGetsNoRegisterAnswer()
    {
        const string Text = """
            .proc p {
                beq body::mid
                .scope body {
                    pha
                    ldx #2
                mid:
                    pla
                    bne @out
                }
            @out:
                rts
            }

            """;

        var analysis = FlowFragment.Analyze("6502", Text);
        Assert.Empty(analysis.Problems());
        var region = analysis.FlowFor("main.nt65")!.Regions.Single();
        Assert.Empty(region.Scopes);
        Assert.Empty(region.ScopeRegisters);
    }

    /// <summary>
    /// A jump to a label inside another routine hands back what the path from that label keeps,
    /// not what the routine keeps from its top. The routine writes X before the label, but the
    /// path from the label leaves X alone. Where the path from the label writes X, the message
    /// says the path has to restore it, since no promise on the routine covers that path.
    /// </summary>
    [Fact]
    public void AJumpToALabelKeepsWhatThePathFromItKeeps()
    {
        Assert.Empty(Problems(
            ".proc owner {\n    ldx #0\ntail:\n    lda #1\n    rts\n}\n.proc p: keeps x {\n    jmp owner::tail\n}\n"));
        Assert.Equal(
            ["main.nt65:8: `p` promises `keeps x`, but X is not the same as on entry here: control does not come "
                + "back from `tail` in `owner`, and the path from there does not keep x: restore it there, "
                + "or add `.next ?` here to end the path unchecked"],
            Problems(".proc owner {\n    lda #1\ntail:\n    ldx #0\n    rts\n}\n.proc p: keeps x {\n    jmp owner::tail\n}\n"));
    }

    private static Registers Kept(string text, string routine) => Found(text, routine).Kept;

    private static RoutineRegisters Found(string text, string routine)
    {
        var analysis = FlowFragment.Analyze("6502", text);
        var flow = analysis.FlowFor("main.nt65");
        Assert.NotNull(flow);
        return flow.Regions.Single(region => region.Routine.DisplayName == routine).Registers;
    }

    private static IReadOnlyList<string> Problems(string text) => FlowFragment.Problems("6502", text);

    /// <summary>
    /// Returns <see cref="Problems"/> for a 65816 program, where a push is as wide as the register
    /// it moves.
    /// </summary>
    private static IReadOnlyList<string> Wide(string text) => FlowFragment.Problems("65816", text);
}
