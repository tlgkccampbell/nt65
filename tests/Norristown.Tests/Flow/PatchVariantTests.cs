using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests <c>.patch @op as …</c>, which lists the instructions a store can turn the patched one
/// into. The analyses take the union of what the written instruction and each variant do, so a
/// flag neither writes survives, and a register either writes is written.
/// </summary>
public sealed class PatchVariantTests
{
    /// <summary>
    /// A flag that neither <c>inx</c> nor <c>dex</c> writes is still known after the patched
    /// instruction, so the branch on it is decided. Without <c>as</c>, the <c>.patch</c> is an
    /// error and nothing is known after the instruction.
    /// </summary>
    [Fact]
    public void AFlagNoVariantWritesSurvives()
    {
        static string Main(string patch) => ".export .proc main {\n    ldy #.opcode(dex)\n    sty @step\n"
            + $"    {patch}\n    sec\n@step:\n    inx\n    bcs @x\n    .byte 1\n@x:\n    rts\n}}\n";

        Assert.Empty(Problems(Main(".patch @step as dex")));
        var problems = Problems(Main(".patch @step"));
        Assert.Contains(problems, problem => problem.Contains("may write the opcode of `@step`", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("falls into this data", StringComparison.Ordinal));
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

    /// <summary>
    /// A variant replaces the opcode, so a store known to write only the operand has none to list.
    /// The operand it writes then says nothing about the flags.
    /// </summary>
    [Fact]
    public void AStoreIntoTheOperandCannotListAVariant()
    {
        var diagnostics = Diagnostics(".export .proc main {\n    lda #0\n"
            + "    sta @op+1\n    .patch @op as and\n@op:\n    lda #0\n    bne @x\n    .byte 1\n@x:\n    rts\n}\n");
        Assert.Contains(diagnostics, diagnostic => diagnostic is
        {
            Id: "patch-variant-rejected",
            Message: "`.patch` cannot list `and` for `lda`: the store writes only the operand, so there is no variant to list",
        });
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "runs-into-data");
    }

    /// <summary>
    /// A store whose offset is not known may write the opcode, so it may list variants. It may
    /// write the operand too, which then says nothing about the flags.
    /// </summary>
    [Theory]
    [InlineData("sta @op,x")]
    [InlineData("sta $10")]
    public void AStoreOfUnknownOffsetMayListAVariant(string store)
    {
        var diagnostics = Diagnostics(".export .proc main {\n    lda #0\n    ldx #0\n"
            + $"    {store}\n    .patch @op as and\n@op:\n    lda #0\n    bne @x\n    .byte 1\n@x:\n    rts\n}}\n");
        Assert.Equal(["runs-into-data"], diagnostics.Select(d => d.Id));
    }

    /// <summary>
    /// A store that may write the opcode has to list what the instruction can become, and each
    /// such store is reported once. Without the list the instruction may be anything, which may
    /// use and change every register. A store into the operand alone leaves the instruction as
    /// written.
    /// </summary>
    [Fact]
    public void AnUnlistedOpcodeIsAnErrorAndMayUseAndChangeEveryRegister()
    {
        static string Main(string store) => ".export .proc main: keeps x, reads a {\n    lda #.opcode(ldx, imm)\n"
            + $"    {store}\n    .patch @op\n@op:\n    lda #5\n    rts\n}}\n";

        Assert.Empty(Diagnostics(Main("sta @op+1")));
        var diagnostics = Diagnostics(Main("sta @op"));
        Assert.Equal(
            ["keeps-broken", "patch-variants-required", "reads-undeclared", "reads-undeclared", "reads-undeclared", "reads-undeclared"],
            diagnostics.Select(d => d.Id).Order());
        Assert.Equal(
            "`sta @op` may write the opcode of `@op`, so the `.patch` must list what the instruction can become with `as`",
            Assert.Single(diagnostics, d => d.Id == "patch-variants-required").Message);
    }

    private static IReadOnlyList<string> Problems(string text) => FlowFragment.Analyze("6502", text).Problems();

    private static IReadOnlyList<Diagnostic> Diagnostics(string text) => FlowFragment.Analyze("6502", text).Diagnostics;
}
