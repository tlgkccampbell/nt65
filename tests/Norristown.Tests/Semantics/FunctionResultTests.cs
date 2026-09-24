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
        var output = Compiled("""
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

    /// <summary>Returns the ca65 a program becomes, which must be a program that compiles.</summary>
    private static string Compiled(string source)
    {
        var compilation = Compiler.Compile([new SourceFile("main.nt65", source)]);
        Assert.Empty(compilation.Diagnostics);
        return Assert.Single(compilation.Ca65).Text;
    }

    /// <summary>Returns the output's data lines joined into one <c>.byte</c> line.</summary>
    private static string Bytes(string output) => ".byte " + string.Join(", ", output.Split('\n')
        .Select(line => line.Split(';')[0].Trim())
        .Where(line => line.StartsWith(".byte ", StringComparison.Ordinal))
        .SelectMany(line => line[".byte ".Length..].Split(',', StringSplitOptions.TrimEntries)));
}
