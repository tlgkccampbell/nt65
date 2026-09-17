using Norristown.Project;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Conditional assembly: what a condition may test, which branch the build takes, and what
/// the declarations under one mean.
/// </summary>
public sealed class ConfigurationTests
{
    /// <summary>The example: one name declared under each of two conditions, and one is real.</summary>
    [Fact]
    public void TheSameNameMayBeDeclaredUnderSeveralConditions()
    {
        const string Source = """
            .module main
            .if PLATFORM == 1 {
            LINES = 262
            }
            .if PLATFORM == 2 {
            LINES = 312
            }
            """;

        Assert.Equal(312, Built(Source, ("PLATFORM", 2)).File("main.nt65").Symbol("LINES").Value.Number);
        Assert.Equal(262, Built(Source, ("PLATFORM", 1)).File("main.nt65").Symbol("LINES").Value.Number);
    }

    /// <summary>A name every branch leaves undeclared is undefined, as with <c>#if</c> in C.</summary>
    [Fact]
    public void ANameNoTakenBranchDeclaresIsUndefined()
    {
        var program = Built("""
            .if PLATFORM == 1 {
            LINES = 262
            }
            TOTAL = LINES
            """, ("PLATFORM", 2));

        Assert.Equal(["main.nt65:5: `LINES` is not declared"], program.Problems());
    }

    /// <summary>A chain takes its first holding branch, whatever the later ones say.</summary>
    [Theory]
    [InlineData(1, 0, "one")]
    [InlineData(0, 3, "two")]
    [InlineData(0, 0, "three")]
    public void AChainTakesOneBranch(long debug, long level, string expected)
    {
        var program = Built("""
            .if DEBUG {
            PICKED = 1
            } .elseif LEVEL > 2 {
            PICKED = 2
            } .else {
            PICKED = 3
            }
            """, ("DEBUG", debug), ("LEVEL", level));

        Assert.Empty(program.Problems());
        Assert.Equal(expected switch { "one" => 1, "two" => 2, _ => 3 },
            program.File("main.nt65").Symbol("PICKED").Value.Number);
    }

    /// <summary>
    /// The right operand of <c>&amp;&amp;</c> and <c>||</c> is neither evaluated nor looked up
    /// once the left decides the result, so a name may be tested for before it is used.
    /// </summary>
    [Fact]
    public void ALogicalOperatorLeavesTheRightOperandAloneOnceTheLeftDecides()
    {
        var program = Built(".if .defined(TRACE) && TRACE {\nON = 1\n}\n");

        Assert.Empty(program.Problems());
        Assert.Empty(program.File("main.nt65").Symbols);
    }

    /// <summary><c>.defined</c> asks about a name without using it, so an unknown one is its answer.</summary>
    [Theory]
    [InlineData(".if .defined(DEBUG) {", true)]
    [InlineData(".if .defined(NOWHERE) {", false)]
    [InlineData(".if !.defined(NOWHERE) {", true)]
    public void DefinedAsksWhetherANameIsADefine(string opener, bool taken)
    {
        var program = Built(opener + "\nON = 1\n.export ON\n}\n", ("DEBUG", 0));

        Assert.Empty(program.Problems());
        Assert.Equal(taken, program.File("main.nt65").Symbols.Count == 1);
    }

    /// <summary>The CPU is configuration, like a define, so a condition may test it.</summary>
    [Fact]
    public void TargetTestsTheProcessor()
    {
        var project = ProjectSettings.None with { Cpu = Cpu.Wdc65C02 };
        var program = Analysis.Program(project, ("main.nt65", """
            .module main
            .segment CODE
            .proc main {
            .if .target(65c02) {
                phx
            } .else {
                txa
                pha
            }
            }
            """));

        Assert.Empty(program.Problems());
    }

    /// <summary>A condition tests the configuration; a check on the program is an assertion.</summary>
    [Theory]
    [InlineData(".if SIZE > 2 {", "`SIZE` is not a define. A condition tests the build configuration, "
        + "and a check on the program is an `.assert`")]
    [InlineData(".if .sizeof(Point) > 2 {", "`.sizeof` asks about the program. A condition tests the "
        + "build configuration, and a check on the program is an `.assert`")]
    public void AConditionThatNamesTheProgramSaysToUseAnAssert(string opener, string message)
    {
        var program = Built("SIZE = 4\n.export SIZE\n" + opener + "\nON = 1\n}\n");

        Assert.Equal([$"main.nt65:4: {message}"], program.Problems());
    }

    /// <summary>
    /// A branch the build leaves out is not read at all, so what it declares does not exist
    /// and what it names is nobody's problem.
    /// </summary>
    [Fact]
    public void ABranchThatIsNotTakenIsNotRead()
    {
        var program = Built("""
            .if 0 {
            BROKEN = nowhere::at::all
            }
            """);

        Assert.Empty(program.Problems());
        Assert.Empty(program.File("main.nt65").Symbols);
    }

    /// <summary>A branch belongs to the scope around it, not to one of its own.</summary>
    [Fact]
    public void ABranchDeclaresIntoTheScopeAroundIt()
    {
        var program = Built("""
            .segment CODE
            .proc main {
            .if 1 {
            @loop:
                jmp @loop
            }
                rts
            }
            """);

        Assert.Empty(program.Problems());
        Assert.Equal("main", program.File("main.nt65").Symbol("@loop").Scope.Name);
    }

    /// <summary>The processor cannot be settled by something that tests the processor.</summary>
    [Fact]
    public void CpuMayNotBeWrittenUnderACondition()
    {
        var program = Built(".if 1 {\n.cpu 65c02\n}\n");

        Assert.Equal(
            ["main.nt65:3: `.cpu` states the program's processor, which a condition may test, "
                + "so it may not be written under an `.if`"],
            program.Problems());
    }

    /// <summary>A continuation with nothing to continue is reported rather than silently taken.</summary>
    [Fact]
    public void AnElseWithNoIfIsReported()
    {
        var program = Built(".scope gfx {\n} .else {\n}\n");

        Assert.Equal(["main.nt65:3: `.else` continues an `.if`, and there is none to continue"],
            program.Problems());
    }

    /// <summary>
    /// The names a condition uses are resolved like any others, so an editor can follow a
    /// define written in one to the configuration that gives it a value.
    /// </summary>
    [Fact]
    public void ADefineNamedInAConditionResolvesToIt()
    {
        var program = Built(".if PLATFORM == 2 {\nON = 1\n.export ON\n}\n", ("PLATFORM", 2));

        Assert.Empty(program.Problems());
        var define = program.File("main.nt65").SymbolAt("PLATFORM");
        Assert.True(define.IsDefine);
        Assert.Equal(2, define.Value.Number);
    }

    /// <summary>Two builds of one file differ only in what the configuration says.</summary>
    [Fact]
    public void OneFileBuiltTwoWaysGivesTwoOutputs()
    {
        const string Source = """
            .segment CODE
            .proc main {
            .if DEBUG {
                jsr trace
            }
                rts
            }
            .proc trace {
                rts
            }
            """;

        var on = Output(Source, ("DEBUG", 1));
        var off = Output(Source, ("DEBUG", 0));

        Assert.Contains("jsr trace", on);
        Assert.DoesNotContain("jsr trace", off);
        Assert.Contains("rts", off);

        // The `.if` itself never reaches ca65, whichever way the build goes.
        Assert.DoesNotContain(".if", on);
        Assert.DoesNotContain(".if", off);
    }

    private static ProgramAnalysis Built(string text, params (string Name, long Value)[] defines) =>
        Analysis.Program(Project(defines), ("main.nt65", ".module main\n" + text));

    private static string Output(string text, params (string Name, long Value)[] defines) =>
        Analysis.Outputs(Project(defines), ("main.nt65", ".module main\n" + text))["main.s"];

    private static ProjectSettings Project((string Name, long Value)[] defines) =>
        ProjectSettings.None with
        {
            Defines = [.. defines.Select(d => new Define(d.Name, d.Value, new Span("nt65.json", 1, 1, 2)))],
        };
}
