using Norristown.LanguageServer;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests <c>.label</c>, which names a position inside an instruction. The bytes from there are
/// decoded as the CPU runs them until they reach the start of an instruction, and the analyses
/// follow what they decode as.
/// </summary>
public sealed class InsideLabelTests
{
    private const string Header = ".module main\n.cpu 6502\n.segment CODE\n";

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
        var output = Analysis.Outputs(ProjectSettings.None, ("main.nt65",
            Header + ".export .proc main {\n@top:\n    lda $E8\n    .label in = @top + 1\n    lsr a\n    bcc in\n    rts\n}\n"))["main.s"];

        Assert.Contains(":= (main__top + $01)", output.Replace("main__main__top", "main__top", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>The hover on the name shows what the bytes from the position run as.</summary>
    [Fact]
    public void TheHoverShowsTheHiddenInstructions()
    {
        const string Text = Header + ".export .proc main {\n@top:\n    sta $0606,x\n    .label in = @top + 2\n    sta ($D1),y\n"
            + "    sec\n    lsr a\n    bcc in\n    rts\n}\n";
        var analysis = Analysis.Program(Analysis.Fragment, (Analysis.Path, Text));

        var hover = Hovers.At(analysis, analysis.File(Analysis.Path), Text.IndexOf("bcc in", StringComparison.Ordinal) + 5);

        Assert.NotNull(hover);
        Assert.Contains("asl $91; cmp ($38),y", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>A position that is not inside an instruction, and bytes nt65 cannot follow, are errors.</summary>
    [Theory]
    [InlineData("lda $10", "@top", "`.label in` has to name a byte inside an instruction of this routine, as `@op + 1` does: its value is not a label plus a number of bytes")]
    [InlineData("lda $10", "@top + 2", "`.label in` has to name a byte inside an instruction of this routine, as `@op + 1` does: the instruction at `@top` is 2 bytes long")]
    [InlineData("lda table", "@top + 1", "nt65 cannot follow the bytes from `in`: they run through the operand of `lda table`, which only the linker knows")]
    [InlineData("lda $60", "@top + 1", "nt65 cannot follow the bytes from `in`: they run as `rts`, which changes where control goes")]
    [InlineData("lda $48", "@top + 1", "nt65 cannot follow the bytes from `in`: they run as `pha`, which moves the stack")]
    [InlineData("lda $3A", "@top + 1", "nt65 cannot follow the bytes from `in`: they run as $3A, which the 6502 has no instruction for, though the 6502x runs it as `nop`, and the 65sc02, the r65c02, the 65c02 and the 65816 run it as `dec`")]
    [InlineData("lda $AD", "@top + 1", "nt65 cannot follow the bytes from `in`: they run past the end of the routine's bytes")]
    public void APositionNt65CannotFollowIsAnError(string instruction, string position, string message)
    {
        var diagnostics = Diagnostics($".export .proc main {{\n@top:\n    {instruction}\n    .label in = {position}\n    rts\n}}\n"
            + ".segment RODATA\n.data table: .byte[2] {\n    1, 2\n}\n");

        Assert.Contains(message, diagnostics.Select(d => d.Message));
    }

    private static IReadOnlyList<Diagnostic> Diagnostics(string text) =>
        Analysis.Program(Analysis.Fragment with { Cpu = Cpu.Mos6502 }, ("main.nt65", Header + text)).Diagnostics;
}
