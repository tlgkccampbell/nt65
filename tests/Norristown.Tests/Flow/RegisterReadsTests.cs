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

    /// <summary>
    /// Code nt65 cannot follow sees only the registers and the stack. Where every register holds
    /// something the routine wrote, and nothing it pushed holds an entry value, such code cannot
    /// read any of the routine's entry values, so the answer stays complete.
    /// </summary>
    [Fact]
    public void ACallThatCannotBeFollowedSeesOnlyWhatIsStillHeld()
    {
        Assert.Equal(new RoutineReads(Registers.None, true), Found("6502",
            ".import print: proc\n.proc p {\n    lda #0\n    ldx #0\n    ldy #0\n    clc\n    jsr print\n    rts\n}\n", "p"));
        Assert.Equal(new RoutineReads(Registers.None, false), Found("6502",
            ".import print: proc\n.proc p {\n    pha\n    lda #0\n    ldx #0\n    ldy #0\n    clc\n    jsr print\n    pla\n    rts\n}\n", "p"));
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

    /// <summary>
    /// A routine whose body is not in the program is trusted to read what it declares, so a caller
    /// is complete. <c>reads none</c> declares that it reads nothing, which leaving <c>reads</c>
    /// out does not.
    /// </summary>
    [Fact]
    public void AnExternThatDeclaresWhatItReadsIsTrusted()
    {
        Assert.Equal(new RoutineReads(Registers.A, true),
            Found("6502", ".proc rom = $FFD2: reads a\n.proc p {\n    jsr rom\n    rts\n}\n", "p"));
        Assert.Equal(new RoutineReads(Registers.None, true),
            Found("6502", ".proc rom = $FFD2: reads none\n.proc p {\n    jsr rom\n    rts\n}\n", "p"));
        Assert.Equal(new RoutineReads(Registers.None, false),
            Found("6502", ".proc rom = $FFD2\n.proc p {\n    jsr rom\n    rts\n}\n", "p"));
    }

    /// <summary>
    /// A caller goes by what a routine declares, not by what its body happens to read, so a
    /// register the routine keeps for later use is still passed to it.
    /// </summary>
    [Fact]
    public void ACallerGoesByTheDeclaration()
    {
        const string Text = ".proc q: reads a, x {\n    sta $10\n    rts\n}\n.proc p {\n    jsr q\n    rts\n}\n";
        Assert.Equal(Registers.A | Registers.X, Read("6502", Text, "q"));
        Assert.Equal(Registers.A | Registers.X, Read("6502", Text, "p"));
        Assert.Empty(Problems(Text));
    }

    /// <summary>
    /// A body that uses a register its <c>reads</c> does not list is reported where the value is
    /// used, which is how a missing <c>clc</c> shows up.
    /// </summary>
    [Fact]
    public void ABodyThatReadsWhatItDoesNotDeclareIsReported()
    {
        Assert.Equal(
            ["main.nt65:2: `add` declares `reads a`, but uses the value its caller left in C: add `c` to `reads`, "
                + "or set the carry with `clc` or `sec` before it is used"],
            Problems(".proc add: reads a {\n    adc #1\n    sta $10\n    rts\n}\n"));
        Assert.Empty(Problems(".proc add: reads a {\n    clc\n    adc #1\n    sta $10\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:6: `p` declares `reads none`, but uses the value its caller left in A through `store`: "
                + "add `a` to `reads`, or give A a value before it is used"],
            Problems(".proc store {\n    sta $10\n    rts\n}\n.proc p: reads none {\n    jsr store\n    rts\n}\n"));
    }

    /// <summary>
    /// A store that a <c>.state saves</c> marks only saves the register, so it is not a use of it,
    /// and the <c>.state keeps</c> where the value is loaded back says the register is kept.
    /// </summary>
    [Fact]
    public void AStoreMarkedAsASaveIsNotARead()
    {
        const string Text = ".proc p: reads none, keeps x {\n    stx $10\n    .state saves x\n    ldx #0\n    stx $11\n"
            + "    ldx $10\n    .state keeps x\n    rts\n}\n";
        Assert.Empty(Problems(Text));
        Assert.Equal(Registers.None, Read("6502", Text.Replace("reads none, ", ""), "p"));

        // The 6502 saves X by way of the accumulator, and the store of A then saves X's value.
        Assert.Equal(Registers.None,
            Read("6502", ".proc p {\n    txa\n    sta $10\n    .state saves x\n    rts\n}\n", "p"));
    }

    /// <summary>
    /// A <c>.state saves</c> must stand directly under a store of the register it names, or of a
    /// register holding the same value.
    /// </summary>
    [Fact]
    public void ASaveMustStandUnderAStoreOfTheRegister()
    {
        Assert.Equal(
            ["main.nt65:3: `saves x` must stand directly under a store of X's value: "
                + "the line above it is not `sta`, `stx` or `sty`"],
            Problems(".proc p {\n    lda #1\n    .state saves x\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:3: `saves x` must stand directly under a store of X's value: "
                + "`sta` stores A, which does not hold X's value there"],
            Problems(".proc p {\n    sta $10\n    .state saves x\n    rts\n}\n"));
    }

    /// <summary>
    /// <c>reads</c> describes a routine and <c>saves</c> one store, so each is an error in the
    /// other's place, and <c>reads</c> belongs before the arrow.
    /// </summary>
    [Fact]
    public void EachItemBelongsInItsOwnPlace()
    {
        Assert.Equal(
            ["main.nt65:1: `saves x` says what one store does, so it belongs in a `.state` directly under that "
                + "store, not in a signature"],
            Problems(".proc p: saves x {\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:2: `reads a` describes a whole routine, not one point in it: put it in the routine's "
                + "signature, not in `.state`"],
            Problems(".proc p {\n    .state reads a\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:1: `reads a` is about a routine from entry to exit, and belongs before `->`"],
            FlowFragment.Problems("65816", ".proc p: a8 -> a16, reads a {\n    rep #$20\n    rts\n}\n"));
    }

    /// <summary>
    /// A call to a label inside another routine reads what the path from that label reads, not
    /// what the routine reads from its top. The routine sets Y before the label, so it reads
    /// nothing, but a call to the label uses the caller's Y.
    /// </summary>
    [Fact]
    public void ACallToALabelReadsWhatThePathFromItReads()
    {
        const string Text = ".proc owner {\n    ldy #0\nshared:\n    lda ($10),y\n    sta $12\n    rts\n}\n"
            + ".proc p {\n    jsr owner::shared\n    rts\n}\n";
        Assert.Equal(new RoutineReads(Registers.None, true), Found("6502", Text, "owner"));
        Assert.Equal(new RoutineReads(Registers.Y, true), Found("6502", Text, "p"));
    }

    /// <summary>
    /// Entered at a label, a routine may pull what its path from the top pushed, and then takes
    /// what its caller pushed instead. So a push before a call to such a label is read, though
    /// the routine entered at its top pulls only what it pushed.
    /// </summary>
    [Fact]
    public void ALabelThatPullsWhatItDidNotPushReadsWhatTheCallerPushed()
    {
        const string Text = ".proc owner {\n    pha\ninner:\n    pla\n    sta $10\n    rts\n}\n"
            + ".proc p {\n    pha\n    jsr owner::inner\n    pla\n    rts\n}\n";
        Assert.Equal(Registers.A, Read("6502", Text, "owner"));
        Assert.Equal(Registers.A, Read("6502", Text, "p"));
    }

    private static IReadOnlyList<string> Problems(string text) => FlowFragment.Problems("6502", text);

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
