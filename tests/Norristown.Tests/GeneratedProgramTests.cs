using System.Text;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests;

/// <summary>
/// Programs nobody wrote, put together from the constructs that have taken the process down
/// rather than the build: an expression nested past reading, a number at the edge of what nt65
/// counts in, a repetition longer than it unrolls, a literal the lexer refuses, and a name that
/// needs itself. It is the net under <see cref="BrokenSourceTests"/>, which can only cut short
/// the sources that exist, and none of these is a line anybody has written yet.
/// <para>
/// A fixed seed and a fixed count: a suite that runs a different program on every build reports
/// a failure nobody can reproduce, and a long run belongs in a person's hands rather than in the
/// edit loop. Nothing about the answers is asserted, because a program nobody wrote has no right
/// diagnostic; what is asked is that reading it and writing it out return at all.
/// </para>
/// </summary>
public sealed class GeneratedProgramTests
{
    /// <summary>What the programs are built from, which does not move.</summary>
    private const int Seed = 6502;

    /// <summary>How many of them there are.</summary>
    private const int Programs = 120;

    [Fact]
    public void AProgramNobodyWroteIsReadAndWrittenOut()
    {
        var programs = Enumerable.Range(0, Programs).Select(Program).ToList();
        var failures = Repo.CollectFailures(programs, Problems);
        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(3)));
    }

    /// <summary>
    /// Reads one program and writes it out, and says so when either throws. The program itself
    /// is part of what is said: it is the whole of what has to be kept to reproduce it.
    /// </summary>
    /// <param name="text">The program.</param>
    private static IEnumerable<string> Problems(string text)
    {
        var problems = new List<string>();
        var tree = SyntaxTree.Parse("generated.nt65", text);
        ProgramAnalysis analysis;
        try
        {
            // An `.incbin` is of unknown length rather than read from disk, as in the
            // truncation test: nothing here asks where the code after one goes.
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

    /// <summary>Program <paramref name="number"/>, which is the same program on every run.</summary>
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
    /// One declaration, written one of the ways that have hurt. Which way is counted out rather
    /// than drawn, so that every kind appears about as often as every other across the
    /// programs, and only what is written inside it is drawn.
    /// </summary>
    /// <param name="random">What the pieces of the declaration are drawn from.</param>
    /// <param name="kind">Which way to write it.</param>
    /// <param name="at">Which declaration of the program it is, which names it.</param>
    private static string Piece(Random random, int kind, int at) => (kind % 8) switch
    {
        0 => $"K{at} = {Nested(random, Number(random))}",
        1 => $"K{at} = {Number(random)} {Operator(random)} {Number(random)}",
        2 => $".data d{at}: .byte {Literal(random)}",
        3 => $".data d{at}: .byte[] {{\n    {Literal(random)}, {Number(random)}\n}}",
        4 => $".data d{at} {{\n    .repeat {Turns(random)}, i {{\n        .byte i\n    }}\n}}",

        // A ring of names, and a type that holds itself with an instance of it: neither the
        // ring nor the type has a value or a size, and emission leaves alone what the analysis
        // has already called cyclic rather than walking the ring until the stack runs out.
        5 => $"A{at} = B{at} {Operator(random)} 1\nB{at} = A{at} + 1",
        6 => $".struct S{at} {{\n    inner: .type S{at}\n}}\n.data s{at}: .type S{at}",
        _ => $".segment CODE {{\n    .export .proc p{at} {{\n"
            + $"        lda #{Nested(random, Number(random))}\n        rts\n    }}\n}}",
    };

    /// <summary>
    /// An expression in parentheses, from one deep to deeper than the parser reads, and as
    /// often as not with none of them closed.
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
    /// A repetition's count. Nothing between a handful and the bound: a count just under it is
    /// a body written out tens of thousands of times, which is slow rather than interesting.
    /// </summary>
    private static string Turns(Random random) => TurnCounts[random.Next(TurnCounts.Length)];

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

    private static readonly string[] TurnCounts =
    [
        "0", "3", "-1", "65537", "$7fffffff", "$7fffffffffffffff", "99999999999999999999",
    ];
}
