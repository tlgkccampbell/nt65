using Norristown.Flow;
using Norristown.Processor;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks which registers a routine uses the entry values of. The tests cover what each
/// instruction uses, values followed through transfers and the stack, what calls pass on, and
/// the cases the analysis cannot follow, which count as reads.
/// </summary>
public sealed class RegisterReadsTests
{
    /// <summary>A routine that writes a register before using it does not read it.</summary>
    [Fact]
    public void ARegisterWrittenBeforeItIsUsedIsNotRead()
    {
        Assert.Equal(new RoutineReads(Registers.None, true), Found("6502", ".proc p {\n    lda #1\n    sta $10\n    rts\n}\n", "p"));
    }

    /// <summary>An instruction reads the registers it uses, including the index its mode adds.</summary>
    [Theory]
    [InlineData("sta $10", Registers.A)]
    [InlineData("adc #1", Registers.A | Registers.C)]
    [InlineData("lda $10,x", Registers.X)]
    [InlineData("lda ($10),y", Registers.Y)]
    [InlineData("asl a", Registers.A)]
    [InlineData("asl $10", Registers.None)]
    [InlineData("rol $10", Registers.C)]
    [InlineData("cpx #1", Registers.X)]
    [InlineData("tax", Registers.None)]
    public void AnInstructionReadsWhatItUses(string instruction, Registers read)
    {
        Assert.Equal(read, Read("6502", $".proc p {{\n    {instruction}\n    rts\n}}\n", "p"));
    }

    /// <summary>A branch on the carry uses it as surely as an addition does.</summary>
    [Fact]
    public void ABranchOnTheCarryReadsIt()
    {
        Assert.Equal(Registers.C, Read("6502", ".proc p {\n    bcc @done\n    nop\n@done:\n    rts\n}\n", "p"));
    }

    /// <summary>A value moved to another register is still the entry value of the one it came from.</summary>
    [Fact]
    public void AValueIsFollowedThroughATransfer()
    {
        Assert.Equal(Registers.X, Read("6502", ".proc p {\n    txa\n    sta $10\n    rts\n}\n", "p"));
        Assert.Equal(Registers.None, Read("6502", ".proc p {\n    txa\n    rts\n}\n", "p"));
    }

    /// <summary>
    /// A save and its restore use nothing, but a value saved on the stack and used from there is
    /// read. A return with a push still on the stack returns through it, which uses it too.
    /// </summary>
    [Fact]
    public void ASaveIsNotARead()
    {
        Assert.Equal(Registers.None, Read("6502", ".proc p {\n    pha\n    lda #1\n    sta $10\n    pla\n    rts\n}\n", "p"));
        Assert.Equal(Registers.A, Read("65c02", ".proc p {\n    pha\n    ldx #0\n    plx\n    stx $10\n    rts\n}\n", "p"));
        Assert.Equal(Registers.A, Read("6502", ".proc p {\n    pha\n    tsx\n    lda $101,x\n    pla\n    rts\n}\n", "p"));
        Assert.Equal(Registers.A, Read("6502", ".proc p {\n    pha\n    rts\n}\n", "p"));
    }

    /// <summary>
    /// A call passes on what the routine it calls reads, of whatever the registers hold at the
    /// call, and a register the callee keeps still holds the caller's entry value after it.
    /// </summary>
    [Fact]
    public void ACallPassesOnWhatItsCalleeReads()
    {
        const string Store = ".proc store {\n    sta $10\n    rts\n}\n";
        Assert.Equal(Registers.A, Read("6502", Store + ".proc p {\n    jsr store\n    rts\n}\n", "p"));
        Assert.Equal(Registers.None, Read("6502", Store + ".proc p {\n    lda #1\n    jsr store\n    rts\n}\n", "p"));
        Assert.Equal(Registers.X, Read("6502", ".proc q {\n    nop\n    rts\n}\n.proc p {\n    jsr q\n    stx $10\n    rts\n}\n", "p"));
        Assert.Equal(Registers.None, Read("6502", ".proc q {\n    ldx #1\n    rts\n}\n.proc p {\n    jsr q\n    stx $10\n    rts\n}\n", "p"));
        Assert.Equal(Registers.A, Read("6502", Store + ".proc p {\n    jmp store\n}\n", "p"));
    }

    /// <summary>Two routines that call each other each read what either uses.</summary>
    [Fact]
    public void RoutinesThatCallEachOtherConverge()
    {
        const string Text = ".proc even {\n    dex\n    beq @done\n    jsr odd\n@done:\n    rts\n}\n"
            + ".proc odd {\n    sty $10\n    jsr even\n    rts\n}\n";
        Assert.Equal(Registers.X | Registers.Y, Read("6502", Text, "even"));
        Assert.Equal(Registers.X | Registers.Y, Read("6502", Text, "odd"));
    }

    /// <summary>A call to a routine whose body is not in the program may read anything.</summary>
    [Fact]
    public void ACallThatCannotBeFollowedIsIncomplete()
    {
        var found = Found("6502", ".import print: proc\n.proc p {\n    ldx #0\n    jsr print\n    rts\n}\n", "p");
        Assert.False(found.Complete);
        Assert.Equal(Registers.All, found.Assumed);
    }

    /// <summary>A routine that takes arguments uses what its caller pushed for it.</summary>
    [Fact]
    public void ARoutineThatTakesArgumentsReadsWhatIsPushed()
    {
        Assert.Equal(Registers.A, Read("65816",
            ".proc f: args 1 {\n    rts\n}\n.proc p: a8 {\n    pha\n    jsr f\n    pla\n    rts\n}\n", "p"));
    }

    /// <summary>
    /// On the 65816 an 8-bit write leaves the accumulator's high byte alone, so a 16-bit use of
    /// the accumulator after it reads the entry value.
    /// </summary>
    [Fact]
    public void AnEightBitWriteLeavesTheHighByteToRead()
    {
        Assert.Equal(Registers.None, Read("65816", ".proc p: a8 {\n    lda #1\n    sta $10\n    rts\n}\n", "p"));
        Assert.Equal(Registers.None, Read("65816", ".proc p: a16 {\n    lda #1\n    xba\n    sta $10\n    rts\n}\n", "p"));
        Assert.Equal(Registers.A, Read("65816", ".proc p: a8 {\n    lda #1\n    xba\n    sta $10\n    rts\n}\n", "p"));
        Assert.Equal(Registers.A, Read("65816",
            ".proc p: a8 -> a16 {\n    lda #1\n    rep #$20\n    sta $10\n    rts\n}\n", "p"));
    }

    /// <summary>An 8-bit save and restore of the accumulator still keeps it whole.</summary>
    [Fact]
    public void AnEightBitSaveStillKeepsTheAccumulator()
    {
        var analysis = FlowFragment.Analyze("65816", ".proc p: a8 {\n    pha\n    lda #1\n    pla\n    rts\n}\n");
        var region = analysis.FlowFor("main.nt65")!.Regions.Single();
        Assert.True(region.Registers.Kept.HasFlag(Registers.A));
        Assert.Equal(Registers.None, region.Reads.Read);
    }

    private static Registers Read(string cpu, string text, string routine) =>
        Found(cpu, text, routine).Read;

    private static RoutineReads Found(string cpu, string text, string routine)
    {
        var analysis = FlowFragment.Analyze(cpu, text);
        var flow = analysis.FlowFor("main.nt65");
        Assert.NotNull(flow);
        return flow.Regions.Single(region => region.Routine.DisplayName == routine).Reads;
    }
}
