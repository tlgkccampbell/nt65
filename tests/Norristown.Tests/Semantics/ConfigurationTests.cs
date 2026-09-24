using Norristown.Processor;
using Norristown.Project;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks conditional assembly, including what a condition may test, which branch the build
/// takes, and what the declarations inside a branch mean.
/// </summary>
public sealed class ConfigurationTests
{
    /// <summary>
    /// When one name is declared under each of two conditions, only the branch the build takes
    /// declares it.
    /// </summary>
    [Fact]
    public void TheSameNameMayBeDeclaredUnderSeveralConditions()
    {
        const string Source = """
            .module main
            .if PLATFORM == 1 {
            .const LINES = 262
            }
            .if PLATFORM == 2 {
            .const LINES = 312
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
            .const LINES = 262
            }
            .const TOTAL = LINES
            """, ("PLATFORM", 2));

        Assert.Equal(["main.nt65:5: `LINES` is not declared"], program.Problems());
    }

    /// <summary>
    /// An <c>.if</c> chain takes the first branch whose condition holds, regardless of the later
    /// conditions.
    /// </summary>
    [Theory]
    [InlineData(1, 0, "one")]
    [InlineData(0, 3, "two")]
    [InlineData(0, 0, "three")]
    public void AChainTakesOneBranch(long debug, long level, string expected)
    {
        var program = Built("""
            .if DEBUG {
            .const PICKED = 1
            } .elseif LEVEL > 2 {
            .const PICKED = 2
            } .else {
            .const PICKED = 3
            }
            """, ("DEBUG", debug), ("LEVEL", level));

        Assert.Empty(program.Problems());
        Assert.Equal(expected switch { "one" => 1, "two" => 2, _ => 3 },
            program.File("main.nt65").Symbol("PICKED").Value.Number);
    }

    /// <summary>
    /// The right operand of <c>&amp;&amp;</c> and <c>||</c> is neither evaluated nor looked up
    /// once the left decides the result.
    /// </summary>
    [Fact]
    public void ALogicalOperatorLeavesTheRightOperandAloneOnceTheLeftDecides()
    {
        var program = Built(".if 0 && NOWHERE {\n.const ON = 1\n}\n");

        Assert.Empty(program.Problems());
        Assert.Empty(program.File("main.nt65").Symbols);
    }

    /// <summary>The CPU is configuration, like a setting, so a condition may test it.</summary>
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
            .const AT_FILE = 1
            .export AT_FILE
            }
            """));

        Assert.Empty(program.Problems());
        Assert.Equal(holds, program.File("main.nt65").Symbols.Any(symbol => symbol.Name == "AT_FILE"));
    }

    /// <summary>
    /// A function asks about the CPU wherever it is called, a data directive's operand included,
    /// and a <c>.select</c> reads only the value it chooses, so the other may name a member that
    /// only the other CPU's build declares.
    /// </summary>
    [Theory]
    [InlineData(Cpu.Mos6502, "$01")]
    [InlineData(Cpu.Wdc65816, "$03")]
    public void TargetAnswersInAFunctionCalledFromData(Cpu cpu, string written)
    {
        var project = ProjectSettings.None with { Cpu = cpu };
        var outputs = Analysis.Outputs(project, ("main.nt65", """
            .module main
            .enum Mode {
                short
                .if .target(65816) {
                    long
                }
            }
            .func size(m) = .select(.target(65816), .select(m == Mode::long, 3, 1), 1)
            .segment RODATA
            .data sizes: .byte[] {
                .each Mode, m {
                    size(m)
                }
            }
            """));

        Assert.Contains(written, outputs["main.s"]);
    }

    /// <summary><c>.has</c> takes a mnemonic, and <c>.target</c> a CPU.</summary>
    [Theory]
    [InlineData(".if .has(LIMIT) {", "`.has` takes a mnemonic, such as `.has(phx)`")]
    [InlineData(".if .target(z80) {", "`.target` takes `6502`, `6502x`, `65sc02`, `r65c02`, `65c02` or `65816`")]
    public void WhatTheCpuQuestionsTakeIsChecked(string opener, string message)
    {
        var program = Built(opener + "\n.const ON = 1\n}\n");

        Assert.Equal([$"main.nt65:2: {message}"], program.Problems());
    }

    /// <summary>
    /// A condition may test a constant, a function or an enum member at file level that the
    /// configuration alone decides, in its own file or in a module that exports it, with no
    /// marker on the declaration.
    /// </summary>
    [Theory]
    [InlineData(".const SIZE = WIDTH * 2\n.if SIZE > 2 {\n.const ON = 1\n}\n", null)]
    [InlineData(".use sizes::SIZE\n.if SIZE > 2 {\n.const ON = 1\n}\n", ".module sizes\n.export SIZE\n.const SIZE = 4\n")]
    [InlineData(".func twice(v) = v * 2\n.if twice(WIDTH) == 6 {\n.const ON = 1\n}\n", null)]
    [InlineData(".enum Mode {\nfast\nslow\n}\n.if WIDTH == Mode::slow + 2 {\n.const ON = 1\n}\n", null)]
    [InlineData(".const PICK = .select(.target(6502), WIDTH, 0)\n.if PICK {\n.const ON = 1\n}\n", null)]
    public void AConditionTestsWhatTheConfigurationDecides(string text, string? other)
    {
        (string, string)[] files = other is null
            ? [("main.nt65", ".module main\n" + text + ".const WIDTH ?= 3\n")]
            : [("main.nt65", ".module main\n" + text + ".const WIDTH ?= 3\n"), ("sizes.nt65", other)];
        var program = Analysis.Program(ProjectSettings.None with { Cpu = Cpu.Mos6502 }, files);

        Assert.DoesNotContain(program.Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        Assert.Contains(program.File("main.nt65").Symbols, symbol => symbol.Name == "ON");
    }

    /// <summary>
    /// A condition that uses a measurement, directly or through the constants built from one, is
    /// told that the configuration does not decide it, with a note for each step to the
    /// measurement.
    /// </summary>
    [Fact]
    public void AConditionThatUsesAMeasurementFollowsTheChain()
    {
        var program = Built("""
            .struct Voice {
                pitch: .byte
            }
            .const per_voice = .sizeof(Voice)
            .const VOICES = per_voice * 2
            .if VOICES > 2 {
            .const ON = 1
            }
            .if .sizeof(Voice) > 2 {
            .const OFF = 1
            }
            """);

        Assert.Equal([
            "main.nt65:7: an `.if` cannot test `VOICES`, which uses a measurement of a declaration; check it with `.assert`",
            "main.nt65:10: an `.if` cannot test `.sizeof(Voice)`, which uses a measurement of a declaration; check it "
                + "with `.assert`",
        ], program.Problems());
        var chain = program.Diagnostics.First(diagnostic => diagnostic.Id == "condition-uses-a-measurement");
        Assert.Equal([
            "6: `VOICES` uses `per_voice`",
            "5: `per_voice` measures `Voice` with `.sizeof`",
        ], chain.Related.Select(note => $"{note.Span.Line}: {note.Message}"));
    }

    /// <summary>
    /// A condition that tests a constant another condition declares is told to declare it once,
    /// with <c>.select</c>.
    /// </summary>
    [Fact]
    public void AConditionThatUsesAConditionalDeclarationSuggestsSelect()
    {
        var program = Built("""
            .if PLATFORM == 1 {
            .const LINES = 262
            }
            .if LINES > 200 {
            .const ON = 1
            }
            """, ("PLATFORM", 1));

        var diagnostic = Assert.Single(program.Diagnostics, d => d.Id == "condition-uses-a-conditional-declaration");
        Assert.Equal("an `.if` cannot test `LINES`, which is declared under another `.if`; declare `LINES` once, with "
            + "`.select`", diagnostic.Message);
        Assert.Equal(["`LINES` is declared under an `.if`"], diagnostic.Related.Select(note => note.Message));
    }

    /// <summary>
    /// A condition that names what is not a value, or a name nothing declares, says so.
    /// </summary>
    [Theory]
    [InlineData(".if here > 2 {", "`here` is not declared")]
    [InlineData(".if main > 2 {", "an `.if` cannot test `main`, which is part of the program; check it with `.assert`")]
    public void AConditionThatNamesTheProgramSuggestsAnAssert(string opener, string message)
    {
        var program = Built(".segment CODE\n.export .proc main {\nrts\n}\n" + opener + "\n.const ON = 1\n}\n");

        Assert.Equal([$"main.nt65:6: {message}"], program.Problems());
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
            .const BROKEN = nowhere::at::all
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

    /// <summary>
    /// <c>.cpu</c> cannot appear under a condition, because a condition may itself test the
    /// processor.
    /// </summary>
    [Fact]
    public void CpuMayNotAppearUnderACondition()
    {
        var program = Built(".if 1 {\n.cpu 65c02\n}\n");

        Assert.Equal(
            ["main.nt65:3: `.cpu` cannot be inside an `.if`: conditions can test the processor, "
                + "so it must be set first"],
            program.Problems());
    }

    /// <summary>A continuation with nothing to continue is reported rather than silently taken.</summary>
    [Fact]
    public void AnElseWithNoIfIsReported()
    {
        var program = Built(".scope gfx {\n} .else {\n}\n");

        Assert.Equal(["main.nt65:3: `.else` has no `.if` before it"],
            program.Problems());
    }

    /// <summary>
    /// A continuation with nothing to continue starts no chain, so the one after it has nothing
    /// to continue either. A data body's size leaves both out, as the build does.
    /// </summary>
    [Fact]
    public void AnElseAfterAnElseWithNoIfIsLeftOutOfASize()
    {
        var program = Built("""
            .segment RODATA
            .data table {
                .repeat 1 {
                    .byte 1
                } .else {
                    .byte 2
                } .else {
                    .byte 3
                }
            }
            .const SIZE = .sizeof(table)
            .export SIZE

            """);

        Assert.Equal(
            ["main.nt65:6: `.else` has no `.if` before it", "main.nt65:8: `.else` has no `.if` before it"],
            program.Problems());
        Assert.Equal(1, program.File("main.nt65").Symbol("SIZE").Value.Number);
    }

    /// <summary>
    /// The names a condition uses are resolved like any others, so an editor can follow a
    /// setting used in one to its declaration and the value the build gives it.
    /// </summary>
    [Fact]
    public void ASettingNamedInAConditionResolvesToIt()
    {
        var program = Built(".if PLATFORM == 2 {\n.const ON = 1\n.export ON\n}\n", ("PLATFORM", 2));

        Assert.Empty(program.Problems());
        var setting = program.File("main.nt65").SymbolAt("PLATFORM");
        Assert.True(setting.IsSetting);
        Assert.Equal(2, setting.Value.Number);
    }

    /// <summary>
    /// The build names a setting by its path, or by its name alone where only one module
    /// declares a setting of that name. A name no module declares, and a name alone that two do,
    /// are reported.
    /// </summary>
    [Theory]
    [InlineData("hw::PAL", 1, null)]
    [InlineData("PAL", 1, null)]
    [InlineData("NTSC", 1, "`NTSC` is not a setting of any module")]
    [InlineData("LINES", 1, "more than one module has a setting `LINES`; name it by its path, as `hw::LINES` or `video::LINES`")]
    [InlineData("hw::WIDE", 1, "`hw::WIDE` is not a setting of any module")]
    public void TheBuildNamesASettingByItsPathOrItsName(string name, long value, string? problem)
    {
        var project = ProjectSettings.None with
        {
            SettingValues = [new SettingValue(name, value, new Span("nt65.json", 1, 1, 2))],
        };
        var program = Analysis.Program(project,
            ("hw.nt65", ".module hw\n.const PAL ?= 0\n.const LINES ?= 1\n.const WIDE = 2\n.export PAL, WIDE\n"),
            ("video.nt65", ".module video\n.const LINES ?= 2\n"));

        if (problem is null)
        {
            Assert.Empty(program.Problems());
            Assert.Equal(value, program.File("hw.nt65").Symbol("PAL").Value.Number);
        }
        else
        {
            Assert.Equal([$"nt65.json:1: {problem}"], program.Problems());
        }
    }

    /// <summary>
    /// A setting's default may use a constant the configuration decides, and one that uses a
    /// measurement is reported with the chain.
    /// </summary>
    [Fact]
    public void ASettingsDefaultMustBeDecidedByTheConfiguration()
    {
        var program = Built("""
            .const BASE = 4
            .const GOOD ?= BASE * 2
            .segment RODATA
            .data table: .byte[4]
            .const BAD ?= .sizeof(table)
            """);

        Assert.Equal(
            ["main.nt65:6: the default of `BAD` uses a value the configuration does not decide"],
            program.Problems());
        Assert.Equal(8, program.File("main.nt65").Symbol("GOOD").Value.Number);
    }

    /// <summary>Two builds of one file differ only in what the configuration decides.</summary>
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

    /// <summary>
    /// Builds <paramref name="text"/> as module <c>main</c>, with a setting declared at its end for
    /// each of <paramref name="settings"/>, which the build gives that value.
    /// </summary>
    private static ProgramAnalysis Built(string text, params (string Name, long Value)[] settings) =>
        Analysis.Program(Project(settings), ("main.nt65", ".module main\n" + text + Declared(settings)));

    private static string Output(string text, params (string Name, long Value)[] settings) =>
        Analysis.Outputs(Project(settings), ("main.nt65", ".module main\n" + text + Declared(settings)))["main.s"];

    private static string Declared((string Name, long Value)[] settings) =>
        string.Concat(settings.Select(setting => $"\n.const {setting.Name} ?= 0\n"));

    private static ProjectSettings Project((string Name, long Value)[] settings) =>
        ProjectSettings.None with
        {
            SettingValues = [.. settings.Select(s => new SettingValue(s.Name, s.Value, new Span("nt65.json", 1, 1, 2)))],
        };
}
