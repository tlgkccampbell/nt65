using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests <c>.patch @op as …</c>, which lists the instructions a store can turn the patched one
/// into. The analyses take the union of what the written instruction and each variant do, so a
/// flag neither writes survives, and a register either writes is written.
/// </summary>
public sealed class PatchVariantTests
{
    private const string Header = ".module main\n.cpu 6502\n.segment CODE\n";

    /// <summary>
    /// A flag that neither <c>inx</c> nor <c>dex</c> writes is still known after the patched
    /// instruction, so the branch on it is decided. Without <c>as</c>, nothing is known after it.
    /// </summary>
    [Fact]
    public void AFlagNoVariantWritesSurvives()
    {
        static string Main(string patch) => ".export .proc main {\n    ldy #.opcode(dex)\n    sty @step\n"
            + $"    {patch}\n    sec\n@step:\n    inx\n    bcs @x\n    .byte 1\n@x:\n    rts\n}}\n";

        Assert.Empty(Problems(Main(".patch @step as dex")));
        Assert.Contains(Problems(Main(".patch @step")), problem => problem.Contains("falls into this data", StringComparison.Ordinal));
    }

    /// <summary>A register only a variant writes is written, so a routine that keeps it breaks its promise.</summary>
    [Fact]
    public void ARegisterAVariantWritesIsWritten()
    {
        static string Main(string patch) => ".export .proc main: keeps y {\n    lda #.opcode(iny)\n    sta @step\n"
            + $"    {patch}\n@step:\n    inx\n    rts\n}}\n";

        Assert.Empty(Diagnostics(Main(".patch @step as dex")));
        Assert.Equal(["keeps-broken"], Diagnostics(Main(".patch @step as dex, iny")).Select(d => d.Id));
    }

    /// <summary>A branch may stand in for a branch, which leaves where control can go as it was.</summary>
    [Fact]
    public void ABranchMayStandInForABranch()
    {
        const string Main = ".export .proc main {\n    lda #.opcode(bne)\n    sta @test\n    .patch @test as bne\n"
            + "    lda $10\n@test:\n    beq @x\n    nop\n@x:\n    rts\n}\n";

        Assert.Empty(Diagnostics(Main));
    }

    /// <summary>A variant the CPU lacks, in the written form or at all, or one the analyses cannot join, is an error.</summary>
    [Theory]
    [InlineData("lda", "`.patch` cannot list `lda` for `inx`: the 6502 has no `lda` in the form `inx` is written in")]
    [InlineData("phx", "`.patch` cannot list `phx` for `inx`: the 6502 has no `phx`")]
    [InlineData("pha", "`.patch` cannot list `pha` for `inx`: `pha` moves the stack, changes the processor's widths or runs a handler")]
    [InlineData("rts", "`.patch` cannot list `rts` for `inx`: a variant has to run on where `inx` runs on, and branch where it branches")]
    [InlineData("LIMIT", "`.patch` cannot list `LIMIT` for `inx`: it is not a mnemonic")]
    public void AVariantThatCannotStandInIsAnError(string variant, string message)
    {
        var diagnostic = Assert.Single(Diagnostics(".export .proc main {\n    lda #0\n    sta @step\n"
            + $"    .patch @step as {variant}\n@step:\n    inx\n    rts\n}}\n"));
        Assert.Equal("patch-variant-rejected", diagnostic.Id);
        Assert.Equal(message, diagnostic.Message);
    }

    private static IReadOnlyList<string> Problems(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", Header + text)).Problems();

    private static IReadOnlyList<Diagnostic> Diagnostics(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", Header + text)).Diagnostics;
}
