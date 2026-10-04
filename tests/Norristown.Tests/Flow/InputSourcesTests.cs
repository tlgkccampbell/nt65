namespace Norristown.Tests.Flow;

/// <summary>
/// Checks where the values that the instruction at the caret reads were set. That is the
/// instruction that wrote each one, the call that left it, the routine's caller, or the line
/// where the analysis lost track. It also checks the lines a value passed through unchanged.
/// </summary>
public sealed class InputSourcesTests
{
    /// <summary>A value written in the routine has the instruction that wrote it as its source.</summary>
    [Fact]
    public void AWriteIsTheSource()
    {
        Assert.Equal(["A: lda #1"], Sources(".proc p {\n    lda #1\n    sta $10\n    rts\n}\n", "sta $10"));
    }

    /// <summary>A value nothing in the routine wrote comes from the routine's caller.</summary>
    [Fact]
    public void AValueNothingWroteComesFromTheCaller()
    {
        Assert.Equal(["X: entry"], Sources(".proc p {\n    stx $10\n    rts\n}\n", "stx $10"));
    }

    /// <summary>Two paths that set the value in different places bring a source each.</summary>
    [Fact]
    public void EachPathBringsItsOwnSource()
    {
        Assert.Equal(
            ["A: lda #1, lda #2"],
            Sources(".proc p {\n    lda #1\n    beq @x\n    lda #2\n@x:\n    sta $10\n    rts\n}\n", "sta $10"));
        Assert.Equal(
            ["A: entry, lda #2"],
            Sources(".proc p {\n    beq @x\n    lda #2\n@x:\n    sta $10\n    rts\n}\n", "sta $10"));
    }

    /// <summary>A transfer is where the register it fills was set.</summary>
    [Fact]
    public void ATransferIsTheSource()
    {
        Assert.Equal(["X: tax"], Sources(".proc p {\n    lda #1\n    tax\n    stx $10\n    rts\n}\n", "stx $10"));
    }

    /// <summary>A value saved and restored passes through the pull, and keeps the source it had before the push.</summary>
    [Fact]
    public void APullPassesTheValueThrough()
    {
        Assert.Equal(
            ["A: lda #1 via pla"],
            Sources(".proc p {\n    lda #1\n    pha\n    lda #2\n    pla\n    sta $10\n    rts\n}\n", "sta $10"));
    }

    /// <summary>
    /// A call that keeps a register passes its value through. A call that does not keep it is
    /// where the value was set, whether the routine returns a value there or only destroys it.
    /// </summary>
    [Fact]
    public void ACallKeepsOrSetsEachRegister()
    {
        const string Q = ".proc q {\n    lda #0\n    rts\n}\n";
        Assert.Equal(
            ["X: ldx #1 via jsr q"],
            Sources(Q + ".proc p {\n    ldx #1\n    jsr q\n    stx $10\n    rts\n}\n", "stx $10"));
        Assert.Equal(
            ["A: call jsr q"],
            Sources(Q + ".proc p {\n    lda #1\n    jsr q\n    sta $10\n    rts\n}\n", "sta $10"));
    }

    /// <summary>
    /// A call nt65 cannot follow, or to a routine that does not say what it keeps, is where the
    /// analysis lost track, and the reason says which.
    /// </summary>
    [Fact]
    public void ACallWithAnIncompleteContractLosesTrack()
    {
        Assert.Equal(
            ["A: ? jsr CHROUT (`CHROUT` does not declare what it keeps)"],
            Sources(".proc CHROUT = $FFD2\n.proc p {\n    lda #1\n    jsr CHROUT\n    sta $10\n    rts\n}\n", "sta $10"));
        Assert.Equal(
            ["X: ldx #1 via jsr CHROUT"],
            Sources(".proc CHROUT = $FFD2: keeps x\n.proc p {\n    ldx #1\n    jsr CHROUT\n    stx $10\n    rts\n}\n", "stx $10"));
    }

    /// <summary>On a call, the inputs are what the routine called reads.</summary>
    [Fact]
    public void ACallsInputsAreWhatTheRoutineReads()
    {
        Assert.Equal(
            ["A: lda #1", "X: entry"],
            Sources(".proc q {\n    sta $10\n    stx $11\n    rts\n}\n.proc p {\n    lda #1\n    jsr q\n    rts\n}\n", "jsr q"));
        Assert.Equal(
            ["A: lda #1", "C: sec"],
            Sources(".proc q: reads a, c {\n    rts\n}\n.proc p {\n    lda #1\n    sec\n    jsr q\n    rts\n}\n", "jsr q"));
    }

    /// <summary>A branch on the carry reads the carry, so the instruction that set the carry is its source.</summary>
    [Fact]
    public void ABranchReadsTheCarry()
    {
        Assert.Equal(["C: cmp #1"], Sources(".proc p {\n    cmp #1\n    bcs @x\n@x:\n    rts\n}\n", "bcs @x"));
    }

    /// <summary>
    /// A branch on N, Z or V reads that flag. The flag's source is the last instruction that wrote
    /// it, or the call that left it. A <c>plp</c> restores the flags a <c>php</c> saved.
    /// </summary>
    [Fact]
    public void ABranchReadsTheFlagItTests()
    {
        Assert.Equal(["Z: lda #1"], Sources(".proc p {\n    lda #1\n    sta $10\n    beq @x\n@x:\n    rts\n}\n", "beq @x"));
        Assert.Equal(["V: bit $10"], Sources(".proc p {\n    bit $10\n    bvs @x\n@x:\n    rts\n}\n", "bvs @x"));
        Assert.Equal(
            ["N: call jsr q"],
            Sources(".proc q {\n    rts\n}\n.proc p {\n    lda #1\n    jsr q\n    bmi @x\n@x:\n    rts\n}\n", "bmi @x"));
        Assert.Equal(
            ["Z: cmp #1 via plp"],
            Sources(".proc p {\n    cmp #1\n    php\n    lda #2\n    plp\n    beq @x\n@x:\n    rts\n}\n", "beq @x"));
    }

    /// <summary>An add reads the carry as well as the accumulator.</summary>
    [Fact]
    public void AnAddReadsTheCarry()
    {
        Assert.Equal(["A: lda #1", "C: clc"], Sources(".proc p {\n    clc\n    lda #1\n    adc #2\n    rts\n}\n", "adc #2"));
    }

    /// <summary>A <c>.state keeps</c> says the register holds its entry value again, and is a through line.</summary>
    [Fact]
    public void AStateKeepsRestoresTheEntryValue()
    {
        Assert.Equal(
            ["X: entry via .state keeps x"],
            Sources(".proc p {\n    stx $10\n    ldx #1\n    ldx $10\n    .state keeps x\n    stx $11\n    rts\n}\n", "stx $11"));
    }

    /// <summary>
    /// An instruction that a macro expansion emitted is shown at the call, which is the line in the
    /// routine. A line of the macro's own definition has no answer, because each expansion of it
    /// would give a different one.
    /// </summary>
    [Fact]
    public void AMacroIsShownAtItsCall()
    {
        const string Text = ".macro one() {\n    lda #1\n    sta $12\n}\n.proc p {\n    one!()\n    sta $10\n    rts\n}\n";
        Assert.Equal(["A: macro one!()"], Sources(Text, "sta $10"));
        Assert.Null(Sources(Text, "sta $12"));
    }

    /// <summary>An instruction that reads nothing has no inputs, and a line with no instruction has no answer.</summary>
    [Fact]
    public void AnInstructionThatReadsNothingHasNoInputs()
    {
        Assert.Equal([], Sources(".proc p {\n    lda #1\n    rts\n}\n", "lda #1"));
        Assert.Null(Sources(".proc p {\n    lda #1\n    rts\n}\n.data b: .byte\n", ".data b: .byte"));
    }

    private static IReadOnlyList<string>? Sources(string text, string line) =>
        FlowFragment.SourcesAt(FlowFragment.Analyze("6502", text), line);
}
