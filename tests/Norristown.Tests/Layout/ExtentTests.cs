using Norristown.Tests.Semantics;

namespace Norristown.Tests.Layout;

/// <summary>
/// Checks <c>.endof</c> and <c>.spanof</c>, which give the address just past something and how
/// many bytes it takes. They describe layout rather than shape, so they are address expressions like any
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

            .data header {
                .word .spanof(reloc)
                .addr .endof(reloc)
            }
            """);

        Assert.Contains("reloc__end:", written, StringComparison.Ordinal);
        Assert.Contains(".word (reloc__end - reloc)", written, StringComparison.Ordinal);
        Assert.Contains(".addr reloc__end", written, StringComparison.Ordinal);
    }

    /// <summary>The end label follows the last byte, and nothing else moves.</summary>
    [Fact]
    public void TheEndLabelComesAfterTheLastByte()
    {
        var written = Written(".proc f {\n    nop\n}\n\n.proc g {\n    rts\n}\n\n.data n: .word .spanof(f)\n");

        Assert.Contains("    nop\nf__end:\n; end of f\n", written, StringComparison.Ordinal);
        Assert.Contains("; .proc g  main.nt65:7\ng:\n", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Data is measured the same way, whether it is a list of elements or a block of mixed
    /// statements.
    /// </summary>
    [Theory]
    [InlineData(".data table: .byte 1, 2, 4, 8\n\n.data n: .word .spanof(table)\n", "table__end", 4)]
    [InlineData(".data blob {\n    .byte 1\n    .data inner: .word 2\n}\n\n.data n: .word .spanof(blob)\n", "blob__end", 3)]
    public void DataIsMeasuredToo(string text, string end, int span)
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
            .module main
            .segment CODE
            .proc irq {
                nop
                nop
                rts
            }

            .assert .spanof(irq) <= 2, "irq handler too big"
            """));

        Assert.Equal(["main.nt65:9: irq handler too big"], program.Problems());
    }

    /// <summary>A span may be used before the thing it measures, as any constant may.</summary>
    [Fact]
    public void ASpanMayBeUsedBeforeWhatItMeasures()
    {
        var program = Analysis.Program(("main.nt65", """
            .module main
            .assert .spanof(irq) == 3, "irq is not three bytes"

            .segment CODE
            .proc irq {
                nop
                nop
                rts
            }
            """));

        Assert.Empty(program.Problems());
    }

    /// <summary>
    /// An <c>.align</c> generates as many bytes as it takes to reach a boundary, so a span
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

            .assert .spanof(f) <= 256, "f too big"
            """);

        Assert.Contains(".assert (f__end - f) <= 256, lderror", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A routine's size is the bytes in its body, so <c>.sizeof</c> of one is its span. It has
    /// no elements, so <c>.countof</c> of one is an error.
    /// </summary>
    [Fact]
    public void SizeofOfARoutineIsItsSpan()
    {
        var program = Analysis.Program(("main.nt65",
            ".module main\n.segment CODE\n.proc f {\n    nop\n    rts\n}\n\n.assert .sizeof(f) == 2, \"f is not two bytes\"\n"
            + ".data n: .byte .countof(f)\n"));

        Assert.Equal(
            ["main.nt65:9: `f` is a routine, which has bytes and no elements: `.sizeof(f)` is how many bytes it takes"],
            program.Problems());
    }

    /// <summary>A label is only a position, and a scope only a namespace: neither has an extent.</summary>
    [Theory]
    [InlineData(".proc f {\n@here:\n    rts\n    .assert .spanof(@here) == 1\n}\n", "main.nt65:6: `@here` is a label, which is only a position: `.spanof` measures a `.data` declaration, a routine or a type")]
    [InlineData(".scope s {\n}\n.data n: .word .endof(s)\n", "main.nt65:5: `s` is a scope, which is only a namespace: `.endof` measures a `.data` declaration, a routine or a type")]
    public void ALabelAndAScopeHaveNoExtent(string text, string problem)
    {
        Assert.Contains(problem, Analysis.Program(("main.nt65", ".module main\n.segment CODE\n" + text)).Problems());
    }

    /// <summary>A constant has no bytes of its own, so there is nothing to measure.</summary>
    [Fact]
    public void MeasuringSomethingWithNoBytesIsAnError()
    {
        var program = Analysis.Program(("main.nt65", ".module main\nN = 5\n\nM = .spanof(N)\n"));

        Assert.Contains(program.Problems(), problem => problem.StartsWith(
            "main.nt65:4: `N` is a constant and takes no bytes of its own", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the ca65 source written for <paramref name="text"/>, which is put in the code
    /// segment.
    /// </summary>
    private static string Written(string text) => Analysis.Outputs(("main.nt65", ".module main\n.segment CODE\n" + text))["main.s"];

    /// <summary>Returns how many bytes layout worked out for the one thing the file measures.</summary>
    private static long? SpanOf(string text)
    {
        var analysis = Analysis.Program(("main.nt65", ".module main\n.segment CODE\n" + text));
        var model = analysis.File("main.nt65");
        var measured = Norristown.Semantics.Extents.MeasuredIn(model).Single();
        return analysis.Layouts.Single().SpanOf(measured);
    }
}
