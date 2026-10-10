using Norristown.LanguageServer;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests <c>.label</c>, which names a position inside an instruction. The bytes from there are
/// decoded as the CPU runs them until they reach the start of an instruction, and the analyses
/// follow what they decode as.
/// </summary>
public sealed class InsideLabelTests
{
    /// <summary>
    /// A branch into the operand of <c>lda $E8</c> runs <c>inx</c>, so a routine that promises
    /// to keep X breaks its promise there, and not where the branch goes to the instruction's start.
    /// </summary>
    [Fact]
    public void TheHiddenInstructionsAreFollowed()
    {
        static string Main(string target) => ".export .proc main: keeps x {\n@top:\n    lda $E8\n    .label in = @top + 1\n"
            + $"    lsr a\n    bcc {target}\n    rts\n}}\n";

        Assert.DoesNotContain("keeps-broken", Diagnostics(Main("@top")).Select(d => d.Id));
        Assert.Equal(["keeps-broken"], Diagnostics(Main("in")).Select(d => d.Id));
    }

    /// <summary>
    /// The bytes may run on into the next instruction, and reach the start of one in the middle
    /// of a run with no label, which is where control goes on.
    /// </summary>
    [Fact]
    public void TheBytesMayReachAnInstructionWithNoLabel()
    {
        const string Main = ".export .proc main {\n@top:\n    sta $0606,x\n    .label in = @top + 2\n    sta ($D1),y\n    sec\n"
            + "    lsr a\n    bcc in\n    rts\n}\n";

        Assert.Empty(Diagnostics(Main));
    }

    /// <summary>The name is written for ca65 as a label at the position the expression gives.</summary>
    [Fact]
    public void TheNameIsWrittenAsALabel()
    {
        var output = FlowFragment.Output(
            "6502", ".export .proc main {\n@top:\n    lda $E8\n    .label in = @top + 1\n    lsr a\n    bcc in\n    rts\n}\n");

        Assert.Contains(":= (main__top + $01)", output.Replace("main__main__top", "main__top", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// The hover on the name shows what the bytes from the position run as, with what each
    /// instruction costs and what they cost in all. On the 6502 <c>asl $91</c> is a direct
    /// read-modify-write at 5 cycles, and <c>cmp ($38),y</c> an indirect-indexed read at 5, or 6
    /// across a page, so the run is 10 to 11.
    /// </summary>
    [Fact]
    public void TheHoverShowsTheHiddenInstructions()
    {
        var analysis = FlowFragment.Analyze("6502", ".export .proc main {\n@top:\n    sta $0606,x\n    .label in = @top + 2\n    sta ($D1),y\n"
            + "    sec\n    lsr a\n    bcc in\n    rts\n}\n");
        var model = analysis.File(Analysis.Path);

        var hover = Hovers.At(analysis, model, model.Offset("bcc in") + 5);

        Assert.NotNull(hover);
        Assert.Contains("asl $91 (5); cmp ($38),y (5-6)", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("10-11 in all", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The limit of 32 bytes applies to where each decoded instruction starts, so a last
    /// instruction that starts inside the limit and ends past it still lands. The bytes from
    /// <c>@top + 1</c> of <paramref name="loads"/> written <c>lda #$A9</c> run one byte out of
    /// step with them, as <paramref name="loads"/> decoded <c>lda #$A9</c>, the last taking its
    /// operand from the opcode of <c>lda $EAA5</c>. Then the operand of that instruction runs as
    /// <c>lda $EA</c>, which starts 2 × loads bytes from the position and lands on <c>rts</c>.
    /// With 15 loads it starts at byte 30 and ends at byte 32, and with 16 it starts at byte 32,
    /// past the limit. An immediate load costs 2 cycles and a direct one 3, so the run costs
    /// 2 × loads + 3.
    /// </summary>
    [Theory]
    [InlineData(14, 31)]
    [InlineData(15, 33)]
    [InlineData(16, null)]
    public void TheLimitAppliesToWhereTheLastInstructionStarts(int loads, int? cycles)
    {
        var text = ".export .proc main {\n    clc\n    bcc in\n@top:\n"
            + string.Concat(Enumerable.Repeat("    lda #$A9\n", loads))
            + "    .label in = @top + 1\n    lda $EAA5\n    rts\n}\n";

        if (cycles is { } count)
        {
            Assert.Equal(new CycleCount(count), HiddenCycles(Cpu.Mos6502, text));
            return;
        }
        Assert.Contains(
            "nt65 cannot follow the bytes from `in`: they do not reach the start of an instruction",
            Diagnostics(text).Select(d => d.Message));
    }

    /// <summary>
    /// A branch to a position inside an earlier instruction does not hop over the <c>jmp</c> after
    /// it, so it is not offered as one branch to the jump's target.
    /// </summary>
    [Fact]
    public void ABranchIntoAnInstructionIsNotABranchOverTheJump()
    {
        var analysis = Analyze(Cpu.Mos6502, ".export .proc main {\n@top:\n    lda $E8\n    .label in = @top + 1\n    lsr a\n"
            + "    bcc in\n    jmp main\n}\n");

        Assert.DoesNotContain(analysis.SuggestionsFor(Analysis.Path), suggestion => suggestion.Id == "branch-over-jump");
    }

    /// <summary>A position that is not inside an instruction, and bytes nt65 cannot follow, are errors.</summary>
    [Theory]
    [InlineData("lda $10", "@top", "`.label in` has to name a byte inside an instruction of this routine, as `@op + 1` does: its value is not a label plus a number of bytes")]
    [InlineData("lda $10", "@top + 2", "`.label in` has to name a byte inside an instruction of this routine, as `@op + 1` does: the instruction at `@top` is 2 bytes long")]
    [InlineData("lda table", "@top + 1", "nt65 cannot follow the bytes from `in`: they run through the operand of `lda table`, which only the linker knows")]
    [InlineData("lda $40", "@top + 1", "nt65 cannot follow the bytes from `in`: they run as `rti`, which changes where control goes")]
    [InlineData("lda $4C", "@top + 1", "nt65 cannot follow the bytes from `in`: they run as `jmp`, which changes where control goes")]
    [InlineData("lda $48", "@top + 1", "nt65 cannot follow the bytes from `in`: they run as `pha`, which moves the stack")]
    [InlineData("lda $3A", "@top + 1", "nt65 cannot follow the bytes from `in`: they run as $3A, which the 6502 has no instruction for, though the 6502x runs it as `nop`, and the 65sc02, the r65c02, the 65c02 and the 65816 run it as `dec`")]
    [InlineData("lda $AD", "@top + 1", "nt65 cannot follow the bytes from `in`: they run past the end of the routine's bytes")]
    public void APositionNt65CannotFollowIsAnError(string instruction, string position, string message)
    {
        var diagnostics = Diagnostics($".export .proc main {{\n@top:\n    {instruction}\n    .label in = {position}\n    rts\n}}\n"
            + ".segment RODATA\n.data table: .byte[2] {\n    1, 2\n}\n");

        Assert.Contains(message, diagnostics.Select(d => d.Message));
    }

    /// <summary>
    /// On the 65816 the hidden instructions are counted in the processor state that reaches the
    /// position. The bytes from <c>@top + 1</c> of <c>lda $10A5</c> run as <c>lda $10</c>, a direct
    /// read. The table gives it 3 cycles, plus 1 for a 16-bit A, and with D declared 0 nothing for
    /// the direct page. Without the state it was 3 to 5.
    /// </summary>
    [Theory]
    [InlineData("a8", 3)]
    [InlineData("a16", 4)]
    public void TheHiddenInstructionsAreCountedInTheProcessorState(string width, int cycles)
    {
        var text = $".export .proc main: native, {width}, i8, dp = 0 {{\n    clc\n    bcc in\n@top:\n    lda $10A5\n"
            + "    .label in = @top + 1\n    rts\n}\n";

        Assert.Equal(new CycleCount(cycles), HiddenCycles(Cpu.Wdc65816, text));
    }

    /// <summary>
    /// On the 65C02 hidden arithmetic pays the decimal-mode cycle only where the decimal flag may
    /// be set. The bytes from <c>@top + 1</c> of <c>lda $69D8</c> and the <c>nop</c> after it run
    /// as <c>cld</c> and <c>adc #$EA</c>, 2 cycles each, because the <c>cld</c> clears D first. From
    /// <c>@top + 2</c> they run as <c>adc #$EA</c> alone, which costs 2 after a <c>cld</c> before the
    /// branch and 3 after a <c>sed</c>. Without the flag it was 2 to 3.
    /// </summary>
    [Theory]
    [InlineData("cld", "@top + 1", 4)]
    [InlineData("cld", "@top + 2", 2)]
    [InlineData("sed", "@top + 2", 3)]
    public void HiddenArithmeticFollowsTheDecimalFlag(string flag, string position, int cycles)
    {
        var text = $".export .proc main {{\n    {flag}\n    clc\n    bcc in\n@top:\n    lda $69D8\n"
            + $"    .label in = {position}\n    nop\n    sta $10\n    rts\n}}\n";

        Assert.Equal(new CycleCount(cycles), HiddenCycles(Cpu.Wdc65C02, text));
    }

    /// <summary>
    /// Bytes that decode as a return end the path there, and their cost counts the return as a
    /// written one does. From the second byte of <c>lda $60E8</c> the processor runs <c>inx</c>,
    /// 2 cycles, then <c>rts</c>, 6 cycles.
    /// </summary>
    [Fact]
    public void AHiddenReturnIsCountedAsAWrittenOne()
    {
        var text = ".export .proc main {\n@top:\n    lda $60E8\n    .label in = @top + 1\n    lsr a\n    bcc in\n    rts\n}\n";

        Assert.Equal(new CycleCount(8), HiddenCycles(Cpu.Mos6502, text));
    }

    /// <summary>
    /// A hidden return leaves the routine as a written one does, so the path through it is a way
    /// out, and the hover lists it among what the bytes run as.
    /// </summary>
    [Fact]
    public void AHiddenReturnLeavesTheRoutine()
    {
        var analysis = Analyze(Cpu.Mos6502, ".export .proc main {\n@top:\n    lda $60\n    .label in = @top + 1\n    lsr a\n    bcc in\n    rts\n}\n");
        Assert.Empty(analysis.Diagnostics);

        var model = analysis.File(Analysis.Path);
        var hidden = analysis.LayoutFor(Analysis.Path)!.HiddenInstructionsOf(model.Symbol("in"));

        Assert.Equal(["rts"], hidden!.Select(instruction => instruction.ToString()));
    }

    /// <summary>Returns what the bytes from the one <c>.label</c> of <paramref name="text"/> cost to run.</summary>
    private static CycleCount? HiddenCycles(Cpu cpu, string text)
    {
        var analysis = Analyze(cpu, text);
        Assert.Empty(analysis.Diagnostics);
        var tree = analysis.File(Analysis.Path).Tree;
        var label = tree.Root.DescendantNodes().OfType<LabelDirectiveSyntax>().Single();
        return analysis.LayoutFor(Analysis.Path)!.Of(label)!.Cycles;
    }

    /// <summary>
    /// Returns the analysis of <paramref name="text"/> after a header that names
    /// <paramref name="cpu"/>, with the project set to the same CPU.
    /// </summary>
    private static ProgramAnalysis Analyze(Cpu cpu, string text) =>
        FlowFragment.Analyze(Analysis.Fragment with { Cpu = cpu }, CpuNames.Format(cpu), (Analysis.Path, text));

    private static IReadOnlyList<Diagnostic> Diagnostics(string text) => Analyze(Cpu.Mos6502, text).Diagnostics;
}
