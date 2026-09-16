using Norristown.Tests.Semantics;

namespace Norristown.Tests.Layout;

/// <summary>
/// <c>.endof</c> and <c>.spanof</c>: the address just past something and how many bytes it
/// takes. They describe layout rather than shape, so they are address expressions like any
/// label difference — resolved by ca65 and ld65 — and never nt65 constants.
/// </summary>
public sealed class ExtentTests
{
    [Fact]
    public void AnEndIsALabelJustPastTheLastByte()
    {
        var written = Written("""
            .proc reloc {
                nop
                rts
            }

            header: .word .spanof(reloc)
                    .addr .endof(reloc)
            """);

        Assert.Contains("reloc__end:", written, StringComparison.Ordinal);
        Assert.Contains(".word (reloc__end - reloc)", written, StringComparison.Ordinal);
        Assert.Contains(".addr reloc__end", written, StringComparison.Ordinal);
    }

    /// <summary>The end label follows the last byte, and nothing else moves.</summary>
    [Fact]
    public void TheEndLabelComesAfterTheLastByte()
    {
        var written = Written(".proc f {\n    nop\n}\n\n.proc g {\n    rts\n}\n\nn: .word .spanof(f)\n");

        Assert.Contains("    nop\nf__end:\n\ng:\n", written, StringComparison.Ordinal);
    }

    /// <summary>A scope and a data declaration are measured the same way.</summary>
    [Theory]
    [InlineData(".scope gfx {\n.proc init {\n    rts\n}\n}\n\nn: .word .spanof(gfx)\n", "gfx__end", 1)]
    [InlineData("table: .byte 1, 2, 4, 8\n\nn: .word .spanof(table)\n", "table__end", 4)]
    public void AScopeAndADataDeclarationAreMeasuredToo(string text, string end, int span)
    {
        var written = Written(text);

        Assert.Contains(end + ":", written, StringComparison.Ordinal);
        Assert.Contains($".word ({end} - ", written, StringComparison.Ordinal);
        Assert.Equal(span, SpanOf(text));
    }

    /// <summary>
    /// nt65 works a span out once the file is laid out, so an assertion about one is
    /// answered at edit time rather than left to the linker.
    /// </summary>
    [Fact]
    public void AnAssertionAboutASpanIsAnsweredAtEditTime()
    {
        var program = Analysis.Program(("main.nt65", """
            .proc irq {
                nop
                nop
                rts
            }

            .assert .spanof(irq) <= 2, error, "irq handler too big"
            """));

        Assert.Equal(["main.nt65:7: irq handler too big"], program.Problems());
    }

    /// <summary>A span may be written before the thing it measures, as any constant may.</summary>
    [Fact]
    public void ASpanMayBeWrittenBeforeWhatItMeasures()
    {
        var program = Analysis.Program(("main.nt65", """
            .assert .spanof(irq) == 3, error, "irq is not three bytes"

            .proc irq {
                nop
                nop
                rts
            }
            """));

        Assert.Empty(program.Problems());
    }

    /// <summary>
    /// An <c>.align</c> generates however many bytes it takes to reach a boundary, so a span
    /// containing one is left to ld65 as a link-time assertion.
    /// </summary>
    [Fact]
    public void ASpanWithAnAlignInItIsLeftToTheLinker()
    {
        var written = Written("""
            .proc f {
                nop
                .align 256
                rts
            }

            .assert .spanof(f) <= 256, lderror, "f too big"
            """);

        Assert.Contains(".assert (f__end - f) <= 256, lderror", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A routine has no shape, only a place in the output, so <c>.sizeof</c> of one is an
    /// error that points at the built-in that does measure it.
    /// </summary>
    [Fact]
    public void SizeofOfARoutineIsAnErrorThatPointsAtSpanof()
    {
        var program = Analysis.Program(("main.nt65", ".proc f {\n    rts\n}\n\nN = .sizeof(f)\n"));

        Assert.Equal(
            ["main.nt65:5: `f` is a routine, and `.sizeof` describes a shape. "
                + "`.spanof` is how many bytes it takes in the output"],
            program.Problems());
    }

    /// <summary>A constant has no bytes of its own, so there is nothing to measure.</summary>
    [Fact]
    public void MeasuringSomethingWithNoBytesIsAnError()
    {
        var program = Analysis.Program(("main.nt65", "N = 5\n\nM = .spanof(N)\n"));

        Assert.Contains(program.Problems(), problem => problem.StartsWith(
            "main.nt65:3: `N` is a constant and takes no bytes of its own", StringComparison.Ordinal));
    }

    private static string Written(string text) => Analysis.Outputs(("main.nt65", text))["main.s"];

    /// <summary>How many bytes layout worked out for the one thing the file measures.</summary>
    private static long? SpanOf(string text)
    {
        var analysis = Analysis.Program(("main.nt65", text));
        var model = analysis.File("main.nt65");
        var measured = Norristown.Semantics.Extents.MeasuredIn(model).Single();
        return analysis.Layouts.Single().SpanOf(measured);
    }
}
