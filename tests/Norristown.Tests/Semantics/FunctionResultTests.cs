namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks that keeping the results of closed functions changes nothing a program shows. A call
/// made again with the same arguments gives the same value, a call with other arguments gives
/// its own, and a problem inside a function is still reported.
/// </summary>
public sealed class FunctionResultTests
{
    /// <summary>
    /// A table built from calls, some repeated and some with a repetition's name as their
    /// argument, writes each call's own value.
    /// </summary>
    [Fact]
    public void EachCallWritesItsOwnValue()
    {
        var output = Analysis.Compiled("""
            .module main
            .cpu 6502
            .func twice(x) = x * 2
            .func dot(row, at) = .strat(row, at) == '#'
            .func pair(row, at) = (dot(row, at) << 1) | dot(row, at + 1)
            .list rows {
                "#..#"
                ".##."
            }
            .segment RODATA
            .data table: .byte[] {
                .repeat 3, i {
                    twice(i), twice(i)
                }
                .each rows, row {
                    pair(row, 0), pair(row, 2), pair(row, 0)
                }
            }
            """);

        Assert.Contains(".byte $00, $00, $02, $02, $04, $04, $02, $01, $02, $01, $02, $01", Bytes(output));
    }

    /// <summary>
    /// A table is sized by the bytes each call gives. A call that gives text is as many elements
    /// as its bytes, whether the text comes straight from the body, through a choice or through
    /// another function, and a call whose body can only be a number is one element.
    /// </summary>
    [Fact]
    public void ACallIsSizedByWhetherItGivesText()
    {
        var output = Analysis.Compiled("""
            .module main
            .cpu 6502
            .func letters(i) = .strcat("a", i + 'b')
            .func either(i) = .select(i, "xy", 7)
            .func passed(i) = letters(i)
            .func scaled(i) = (i * 3) + 1
            .func nested(i) = scaled(i) - 1
            .segment RODATA
            .data table: .byte[15] {
                .repeat 2, i {
                    letters(i), either(i), passed(i), scaled(i), nested(i)
                }
            }
            """);

        Assert.Contains(
            ".byte $61, $62, $07, $61, $62, $01, $00, $61, $63, $78, $79, $61, $63, $04, $03",
            Bytes(output));
    }

    /// <summary>
    /// A function that meets a problem is evaluated again at each call, so the problem is
    /// reported whichever call is evaluated first.
    /// </summary>
    [Fact]
    public void AProblemInsideAFunctionIsStillReported()
    {
        var compilation = Compiler.Compile([new SourceFile("main.nt65", """
            .module main
            .cpu 6502
            .func inverse(x) = 1 / x
            .segment RODATA
            .data table: .byte[] {
                inverse(0), inverse(0)
            }
            """)]);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "division by zero");
    }

    /// <summary>Returns the output's data lines joined into one <c>.byte</c> line.</summary>
    private static string Bytes(string output) => ".byte " + string.Join(", ", output.Split('\n')
        .Select(line => line.Split(';')[0].Trim())
        .Where(line => line.StartsWith(".byte ", StringComparison.Ordinal))
        .SelectMany(line => line[".byte ".Length..].Split(',', StringSplitOptions.TrimEntries)));
}
