using Norristown.Processor;
using Norristown.Project;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Conditional assembly: what a condition may test, which branch the build takes, and what
/// the declarations inside a branch mean.
/// </summary>
public sealed class ConfigurationTests
{
    /// <summary>One name declared under each of two conditions: only the branch the build takes declares it.</summary>
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

    /// <summary>An <c>.if</c> chain takes the first branch whose condition holds, whatever the later ones say.</summary>
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

    /// <summary><c>.defined</c> asks whether a name is a define without using it, so an unknown name is a false answer, not an error.</summary>
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
            .export .proc main {
            .if .target(65c02) {
                phx
            } .else {
                txa
                pha
            }
                rts
            }
            """));

        Assert.Empty(program.Problems());
    }

    /// <summary>
    /// <c>.has</c> asks whether the CPU has an instruction, whichever CPU it is, and
    /// <c>.target</c> names one exactly.
    /// </summary>
    [Theory]
    [InlineData(Cpu.Mos6502, ".has(phx)", false)]
    [InlineData(Cpu.Cmos65SC02, ".has(phx)", true)]
    [InlineData(Cpu.Cmos65SC02, ".has(rmb0)", false)]
    [InlineData(Cpu.Rockwell65C02, ".has(rmb0) && !.has(wai)", true)]
    [InlineData(Cpu.Wdc65816, ".has(wai) && !.has(rmb0)", true)]
    [InlineData(Cpu.Mos6502, ".has(jeq)", true)]
    [InlineData(Cpu.Rockwell65C02, ".target(r65c02)", true)]
    [InlineData(Cpu.Wdc65C02, ".target(65sc02)", false)]
    public void HasAsksWhetherTheCpuHasAnInstruction(Cpu cpu, string condition, bool holds)
    {
        var project = ProjectSettings.None with { Cpu = cpu };
        var program = Analysis.Program(project, ("main.nt65", $$"""
            .module main
            .if {{condition}} {
            AT_FILE = 1
            .export AT_FILE
            }
            """));

        Assert.Empty(program.Problems());
        Assert.Equal(holds, program.File("main.nt65").Symbols.Any(symbol => symbol.Name == "AT_FILE"));
    }

    /// <summary><c>.has</c> takes a mnemonic, and <c>.target</c> a CPU.</summary>
    [Theory]
    [InlineData(".if .has(LIMIT) {", "`.has` takes a mnemonic, such as `.has(phx)`")]
    [InlineData(".if .target(z80) {", "`.target` takes `6502`, `6502x`, `65sc02`, `r65c02`, `65c02` or `65816`")]
    public void WhatTheCpuQuestionsTakeIsChecked(string opener, string message)
    {
        var program = Built(opener + "\nON = 1\n}\n");

        Assert.Equal([$"main.nt65:2: {message}"], program.Problems());
    }

    /// <summary>A condition tests the configuration; a check on the program is an assertion.</summary>
    [Theory]
    [InlineData(".if SIZE > 2 {", "`SIZE` is not a define or a `.config`. A condition tests the build configuration, "
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
    /// and a name it uses that does not exist is not reported.
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
            .export .proc main {
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

    /// <summary><c>.cpu</c> cannot be written under a condition, because a condition may itself test the processor.</summary>
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
            .export .proc main {
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
