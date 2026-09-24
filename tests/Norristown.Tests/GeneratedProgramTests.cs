using System.Text;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests;

/// <summary>
/// Tests generated programs, put together from the kinds of construct that have crashed nt65
/// instead of producing diagnostics. These are an expression nested deeper than the parser
/// reads, a number at the edge of nt65's integer range, a repetition longer than nt65 will
/// unroll, a literal the lexer rejects, and a name defined in terms of itself. The test is a
/// safety net beneath <see cref="BrokenSourceTests"/>, which can only cut short the sources that
/// exist, and none of these lines appears in any of them.
/// <para>
/// The seed and the count are fixed. A suite that runs a different program on every build
/// reports failures nobody can reproduce, and a long randomized run is for a person to start
/// deliberately, not for the edit loop. The diagnostics are not checked, because a generated
/// program has no single right diagnostic. The test asks only that analysing and emitting it do
/// not throw.
/// </para>
/// </summary>
public sealed class GeneratedProgramTests
{
    /// <summary>The fixed random seed the programs are generated from.</summary>
    private const int Seed = 6502;

    /// <summary>How many programs are generated.</summary>
    private const int Programs = 120;

    [Fact]
    public void ARandomProgramIsAnalyzedAndEmittedWithoutThrowing()
    {
        var programs = Enumerable.Range(0, Programs).Select(Program).ToList();
        var failures = Repo.CollectFailures(programs, Problems);
        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(3)));
    }

    /// <summary>
    /// Analyses one program and emits it, and returns a failure message when either throws. The
    /// message includes the program's text, which is all that is needed to reproduce the failure.
    /// </summary>
    /// <param name="text">The program.</param>
    private static IEnumerable<string> Problems(string text)
    {
        var problems = new List<string>();
        var tree = SyntaxTree.Parse("generated.nt65", text);
        ProgramAnalysis analysis;
        try
        {
            // An `.incbin` is given an unknown length instead of being read from disk, as in
            // BrokenSourceTests, because nothing here depends on where the code after one is laid out.
            analysis = Compiler.Analyze([tree], ProjectSettings.None, _ => (long?)null);
        }
        catch (Exception e)
        {
            return [$"analyzing throws {e.GetType().Name}: {e.Message}\n{text}"];
        }
        try
        {
            Compiler.Emit(analysis, ProjectSettings.None);
        }
        catch (Exception e)
        {
            problems.Add($"emitting throws {e.GetType().Name}: {e.Message}\n{text}");
        }
        return problems;
    }

    /// <summary>Builds program <paramref name="number"/>, which is the same program on every run.</summary>
    /// <param name="number">Which program to build.</param>
    private static string Program(int number)
    {
        var random = new Random(Seed + number);
        var text = new StringBuilder(".module generated\n\n.segment CODE: abs\n\n");
        var pieces = 3 + random.Next(5);
        for (var i = 0; i < pieces; i++)
            text.Append(Piece(random, number + i, i)).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Builds one declaration, of one of the kinds that have caused crashes. The kind is chosen in
    /// rotation instead of at random, so that every kind appears about equally often across the
    /// programs. Only the declaration's contents are random.
    /// </summary>
    /// <param name="random">The random source for the declaration's contents.</param>
    /// <param name="kind">Which kind of declaration to build.</param>
    /// <param name="at">The declaration's position in the program, which its name includes.</param>
    private static string Piece(Random random, int kind, int at) => (kind % 8) switch
    {
        0 => $"K{at} = {Nested(random, Number(random))}",
        1 => $"K{at} = {Number(random)} {Operator(random)} {Number(random)}",
        2 => $".data d{at}: .byte {Literal(random)}",
        3 => $".data d{at}: .byte[] {{\n    {Literal(random)}, {Number(random)}\n}}",
        4 => $".data d{at} {{\n    .repeat {IterationCount(random)}, i {{\n        .byte i\n    }}\n}}",

        // A cycle of constants has no value, and a struct containing an instance of itself has
        // no size. Emission must skip what the analysis has already reported as cyclic instead of
        // following the cycle until the stack overflows.
        5 => $"A{at} = B{at} {Operator(random)} 1\nB{at} = A{at} + 1",
        6 => $".struct S{at} {{\n    inner: .type S{at}\n}}\n.data s{at}: .type S{at}",
        _ => $".segment CODE {{\n    .export .proc p{at} {{\n"
            + $"        lda #{Nested(random, Number(random))}\n        rts\n    }}\n}}",
    };

    /// <summary>
    /// Builds an expression in parentheses, nested from one level to deeper than the parser reads.
    /// Half the time, none of the parentheses is closed.
    /// </summary>
    private static string Nested(Random random, string inner)
    {
        var depth = Depths[random.Next(Depths.Length)];
        return new string('(', depth) + inner + (random.Next(2) == 0 ? new string(')', depth) : "");
    }

    private static string Number(Random random) => Numbers[random.Next(Numbers.Length)];

    private static string Operator(Random random) => Operators[random.Next(Operators.Length)];

    private static string Literal(Random random) => Literals[random.Next(Literals.Length)];

    /// <summary>
    /// Returns a repetition's count. No count falls between a handful and the limit, because a
    /// count just under the limit unrolls the body tens of thousands of times, which is slow
    /// rather than interesting.
    /// </summary>
    private static string IterationCount(Random random) => IterationCounts[random.Next(IterationCounts.Length)];

    private static readonly int[] Depths = [1, 2, 99, 100, 101, 600, 800];

    private static readonly string[] Numbers =
    [
        "0", "1", "(0 - 1)", "$7fffffffffffffff", "(0 - $7fffffffffffffff - 1)",
        "99999999999999999999", "$10000", "70", "$1G", "%102",
    ];

    private static readonly string[] Operators = ["/", ".mod", "*", "+", "-", "<<", ">>"];

    private static readonly string[] Literals =
    [
        "1", "'a'", @"'\xZZ'", @"'\q'", @"""\x4""", "'ab'", "''", @"""unterminated", "'é'",
    ];

    private static readonly string[] IterationCounts =
    [
        "0", "3", "-1", "65537", "$7fffffff", "$7fffffffffffffff", "99999999999999999999",
    ];
}
