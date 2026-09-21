using Norristown.Tests.Semantics;

namespace Norristown.Tests.Layout;

/// <summary>
/// How far a branch reaches, and what a long branch is written as. The distance is known
/// when the branch and its target sit in one stream of bytes with no <c>.align</c> between
/// them; where it is not, ca65's own check stands and a long branch takes its long form.
/// </summary>
public sealed class BranchTests
{
    /// <summary>Filler of <paramref name="bytes"/> bytes, one <c>nop</c> each.</summary>
    private static string Nops(int bytes) => $".repeat {bytes}, i {{\n        nop\n    }}\n";

    [Fact]
    public void AForwardLongBranchWithinRangeIsTheShortBranch()
    {
        var written = Written($".proc p {{\n    jeq @out\n    {Nops(127)}@out:\n    rts\n}}\n");

        Assert.Contains("    beq p__out", written, StringComparison.Ordinal);
        Assert.DoesNotContain("    jmp", written, StringComparison.Ordinal);
    }

    [Fact]
    public void AForwardLongBranchOutOfRangeIsTheInvertedBranchOverAJump()
    {
        var written = Written($".proc p {{\n    jeq @out\n    {Nops(128)}@out:\n    rts\n}}\n");

        Assert.Contains("    bne p__over\n    jmp p__out\np__over:\n", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lengthening a branch moves everything after it, which can put a branch before it out
    /// of reach. That is why the file is laid out again until nothing changes, and it
    /// terminates because a branch only ever grows.
    /// </summary>
    [Fact]
    public void LengtheningOneBranchCanPutAnotherOutOfReach()
    {
        var written = Written(
            $".proc p {{\n    jeq @out\n    jeq @far\n    {Nops(123)}@out:\n    rts\n    {Nops(130)}@far:\n    rts\n}}\n");

        // On its own the first reaches 125 bytes and is in range; once the second has grown
        // by the three bytes of its `jmp`, it reaches 128 and is not.
        Assert.Equal(2, Occurrences(written, "    jmp p__"));
        Assert.DoesNotContain("    beq", written, StringComparison.Ordinal);
    }

    /// <summary>A target at a distance nt65 does not know is always long.</summary>
    [Fact]
    public void ATargetAtAnUnknownDistanceIsLong()
    {
        var written = Written(".proc p {\n    jeq @out\n    rts\n    .align 256\n@out:\n    rts\n}\n");

        Assert.Contains("    bne p__over\n    jmp p__out\n", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Backwards, to a target already written. This is the one case ca65's own package can
    /// also shorten, because by then it knows where the target landed.
    /// </summary>
    [Fact]
    public void ABackwardLongBranchWithinRangeIsTheShortBranch()
    {
        var written = Written(".proc p {\n@top:\n    nop\n    jne @top\n    rts\n}\n");

        Assert.Contains("    bne p__top", written, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortBranchThatCannotReachItsTargetIsReported()
    {
        var program = Analysis.Program(
            ("main.nt65", $".module main\n.segment CODE\n.proc p {{\n    beq @out\n    {Nops(128)}@out:\n    rts\n}}\n"));

        Assert.Equal(
            ["main.nt65:4: `beq` would branch 128 bytes, and a branch reaches only -128 to 127. "
                + "`jeq` reaches any near target"],
            program.Problems());
    }

    /// <summary>
    /// A branch whose distance nt65 cannot work out is left to ca65, which sees the
    /// addresses nt65 never does.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    beq @out\n    rts\n    .align 256\n@out:\n    rts\n}\n")]
    [InlineData(".proc p {\n    beq elsewhere\n    rts\n}\n\n.proc elsewhere {\n    rts\n}\n")]
    public void ADistanceNt65DoesNotKnowIsNotReported(string text)
    {
        Assert.Empty(Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.segment CODE\n" + text)).Problems());
    }

    /// <summary>
    /// A nested segment block is a detour from the stream around it, so the branch and its
    /// target stay a known distance apart and the bytes in the detour are not between them.
    /// </summary>
    [Fact]
    public void ANestedSegmentBlockIsNotBetweenABranchAndItsTarget()
    {
        var text = ".module main\n.segment CODE\n.proc p {\n    beq @out\n    .segment RODATA {\n    .data table: .byte[200]\n    }\n@out:\n    lda table\n    rts\n}\n";

        Assert.Empty(Analysis.Program(Analysis.Fragment, ("main.nt65", text)).Problems());
    }

    /// <summary>The output for <paramref name="text"/>, which is placed in the code segment.</summary>
    private static string Written(string text) => Analysis.Outputs(("main.nt65", ".module main\n.segment CODE\n" + text))["main.s"];

    private static int Occurrences(string text, string find)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(find, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += find.Length;
        }
        return count;
    }
}
