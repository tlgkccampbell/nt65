using System.Text.RegularExpressions;
using Norristown.Project;
using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Oracle;

/// <summary>Holds the tests that run the pinned ca65. <c>scripts/test.ps1 -Ca65</c> runs only these.</summary>
[Trait("Category", "Oracle")]
public sealed partial class OracleTests
{
    /// <summary>
    /// Hand-written ca65 files in tests/oracle. A trailing <c>;= N</c> on a line says ca65
    /// must generate N bytes for it, which exercises the listing comparison independently of
    /// nt65's own output.
    /// </summary>
    [Fact]
    public void HandWrittenFilesAssembleCleanly()
    {
        Assert.SkipWhen(Repo.Selection is not null, "NT65_FIXTURE selects fixtures and programs, and these files are neither");
        var files = Directory.GetFiles(Repo.Path("tests", "oracle"), "*.s").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        var failures = Repo.CollectFailures(files, Check);
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        static IEnumerable<string> Check(string path)
        {
            var name = Path.GetFileName(path);
            var source = Repo.ReadText(path);
            var result = Ca65Oracle.Pinned.Assemble(name, source);
            if (!result.Succeeded)
            {
                yield return $"{name}: ca65 reported:\n{result.Messages}";
                yield break;
            }
            var lines = source.ReplaceLineEndings("\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (ByteAnnotation().Match(lines[i]) is { Success: true } m
                    && int.Parse(m.Groups[1].Value) is var want && result.LineBytes[i] != want)
                {
                    yield return $"{name}:{i + 1}: expected {want} bytes, ca65 generated {result.LineBytes[i]}";
                }
            }
        }
    }

    /// <summary>
    /// Everything nt65 generates must assemble with no errors and no warnings, because a ca65
    /// diagnostic on nt65 output is an nt65 bug. ca65 must also generate exactly as many bytes
    /// for each line as nt65 computed for it. Branch ranges, cycle counts and assertions are
    /// built on those lengths, so agreeing with the assembler about them is the check that
    /// matters.
    /// </summary>
    [Fact]
    public void GeneratedOutputAssemblesToTheLengthsNt65Computed()
    {
        var fixtures = FixtureCase.All();
        var outputs = fixtures
            .SelectMany(fixture => Compiler.Compile(fixture.Sources, fixture.Project, fixture.BinaryLength)
                .Ca65.Select(o => (Fixture: fixture, Output: o)))
            .ToList();
        Repo.RequireAny(outputs);
        Assert.Contains(outputs, o => o.Output.LineBytes.Any(bytes => bytes > 0));

        // Every fixture with no errors of its own must reach the assembler, because a fixture
        // that silently produced nothing would be checked by nobody. A fixture that expects no
        // output, such as one whose modules have nothing to write, is checked for that by the
        // fixture runner.
        var produced = outputs.Select(o => o.Fixture.Name).ToHashSet(StringComparer.Ordinal);
        var missing = fixtures
            .Where(f => f.ExpectedDiagnostics().Count == 0 && f.Sources.Count > 0 && f.ExpectedOutputs().Count > 0
                && !produced.Contains(f.Name))
            .Select(f => f.Name)
            .ToList();
        Assert.True(missing.Count == 0, $"produced no output, so nothing checked it: {string.Join(", ", missing)}");

        var failures = Repo.CollectFailures(outputs,
            o => AssemblesToComputedLengths(
                o.Fixture.Name, o.Output, o.Output.Path, o.Fixture.Binaries()));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// A fixture with a <c>link/</c> directory holds a hand-written ca65 module and a linker
    /// configuration. nt65's output must link against it. The object file is the only
    /// boundary between the two, so what nt65 exports has to be what ca65 imports, and the
    /// other way round.
    /// </summary>
    [Fact]
    public void GeneratedOutputLinksWithHandWrittenCa65()
    {
        var linkable = FixtureCase.All().Where(fixture => LinkFiles(fixture) is not null).ToList();
        Repo.RequireAny(linkable);

        var failures = Repo.CollectFailures(linkable, Check);
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        static IEnumerable<string> Check(FixtureCase fixture)
        {
            var (config, handWritten) = LinkFiles(fixture)!.Value;
            var compilation = Compiler.Compile(fixture.Sources, fixture.Project);
            var result = Ca65Oracle.Pinned.Link(config,
                [.. compilation.Ca65.Select(o => (Path.GetFileName(o.Path), o.Text)), .. handWritten]);
            if (!result.Succeeded)
                yield return $"[{fixture.Name}] ld65 reported:\n{result.Messages}";
            else if (result.Binary.Length == 0)
                yield return $"[{fixture.Name}] linked, but wrote no bytes";
        }
    }

    /// <summary>
    /// A checked import is a promise nt65 made about a value it has already used in its own
    /// arithmetic, and the linker enforces it. When the same program is linked against a module
    /// that defines the symbol differently, ld65 must refuse it.
    /// </summary>
    [Fact]
    public void ACheckedImportWithTheWrongValueFailsTheLink()
    {
        Repo.SkipUnlessSelected("modules");
        var fixture = FixtureCase.All().Single(f => f.Name == "modules");
        var (config, handWritten) = LinkFiles(fixture)!.Value;
        var wrong = handWritten
            .Select(file => (file.Name, Source: file.Source.Replace("HOST_VERSION = $0102", "HOST_VERSION = $0103")))
            .ToList();
        Assert.Contains(wrong, file => file.Source.Contains("$0103"));

        var result = Ca65Oracle.Pinned.Link(config,
            [.. Compiler.Compile(fixture.Sources, fixture.Project).Ca65
                .Select(o => (Path.GetFileName(o.Path), o.Text)),
             .. wrong]);

        Assert.False(result.Succeeded);
        Assert.Contains("HOST_VERSION is not $0102", result.Messages);
    }

    /// <summary>
    /// An initialized struct instance is written as one directive per member, and the bytes it
    /// produces are the bytes a programmer would have written by hand. Assembling both and
    /// comparing the linked images shows the layout is right rather than merely plausible.
    /// </summary>
    [Fact]
    public void AnInitializedInstanceAssemblesToTheBytesWrittenByHand()
    {
        const string Nt65 = """
            .module main
            .struct Point {
            x:      .word
            y:      .word
            }

            .struct Actor {
            pos:    .type Point
            hp:     .byte
            name:   .res 4
            }

            .segment RODATA
            .data boss:   .type Actor { pos = { x = 100, y = 40 }, hp = 99, name = "ZIP" }

            .data hero:   .type Actor {
                hp = 3
                pos = { y = 7 }
            }
            """;

        const string ByHand = """
            .setcpu "6502"
            .segment "RODATA": absolute
            boss:
                .word 100
                .word 40
                .byte 99
                .byte $5a, $49, $50, $00
            hero:
                .word 0
                .word 7
                .byte 3
                .byte $00, $00, $00, $00
            """;

        var generated = Compiler.Compile([new SourceFile("main.nt65", Nt65)]);
        Assert.Empty(generated.Diagnostics);

        LinksLikeByHand(Assert.Single(generated.Ca65).Text, ByHand);
    }

    /// <summary>
    /// A placed module's bytes land where its <c>.place</c> line is, in every segment it writes
    /// to. In CODE they land between the routines on either side of that line. In RODATA they
    /// land after what the placing file had written there and before what it writes next. The
    /// linked image is checked byte for byte against the same program written by hand as one file.
    /// </summary>
    [Fact]
    public void APlacedModulesBytesLandWhereItsPlaceStands()
    {
        const string Main = """
            .module main
            .segment RODATA
            .data before: .byte $01
            .segment CODE
            .export .proc start {
                lda #$aa
                rts
            }
            .place part
            .export .proc finish {
                lda #$bb
                rts
            }
            .segment RODATA
            .data after: .byte $03
            """;
        const string Part = """
            .module part: placed
            .segment RODATA
            .data middle: .byte $02
            .segment CODE
            .export .proc between {
                lda #$cc
                rts
            }
            """;
        const string ByHand = """
            .setcpu "6502"
            .segment "RODATA": absolute
                .byte $01
            .segment "CODE": absolute
                lda #$aa
                rts
            .segment "RODATA": absolute
                .byte $02
            .segment "CODE": absolute
                lda #$cc
                rts
                lda #$bb
                rts
            .segment "RODATA": absolute
                .byte $03
            """;

        var generated = Compiler.Compile(
            [new SourceFile("main.nt65", Main), new SourceFile("part.nt65", Part)],
            ProjectSettings.None with { Cpu = Processor.Cpu.Mos6502 });
        Assert.DoesNotContain(generated.Diagnostics, d => d.Severity == Severity.Error);

        var linked = LinksLikeByHand(Assert.Single(generated.Ca65).Text, ByHand);
        Assert.Equal([0xa9, 0xaa, 0x60, 0xa9, 0xcc, 0x60, 0xa9, 0xbb, 0x60, 0x01, 0x02, 0x03], linked);
    }

    /// <summary>
    /// A <c>.repeat</c> in the body of a macro that another module exports is unrolled in the
    /// module that calls it. In each iteration the loop variable is the iteration number there,
    /// just as in the macro's own module. Each iteration is therefore written as the bytes it
    /// produces, not as the body's text with the variable left in, which ca65 would read as an
    /// undeclared symbol. The linked image is the text's bytes with the last byte's top bit set,
    /// and so is the image of the same call to a macro defined in the calling module.
    /// </summary>
    [Fact]
    public void ARepetitionInAnotherModulesMacroCountsWhereItIsCalled()
    {
        const string Library = """
            .module lib
            .export htasc
            .macro htasc(text) {
                .repeat .strlen(text) - 1, i {
                    .byte .strat(text, i)
                }
                .byte .strat(text, .strlen(text) - 1) | $80
            }
            """;
        const string Main = """
            .module main
            .use lib::htasc
            .macro athome(text) {
                .repeat .strlen(text) - 1, i {
                    .byte .strat(text, i)
                }
                .byte .strat(text, .strlen(text) - 1) | $80
            }
            .segment RODATA
            .data words {
                htasc!("NEXT WITHOUT FOR")
                athome!("FOR")
            }
            """;

        var config = Repo.ReadText(Repo.Path("tests", "fixtures", "modules", "link", "link.cfg"));
        var generated = Compiler.Compile(
            [new SourceFile("main.nt65", Main), new SourceFile("lib.nt65", Library)],
            ProjectSettings.None with { Cpu = Processor.Cpu.Mos6502 });
        Assert.Empty(generated.Diagnostics);
        var output = generated.Ca65.Single(o => o.Path.EndsWith("main.s", StringComparison.Ordinal));
        Assert.DoesNotContain(".strat", output.Text, StringComparison.Ordinal);

        var linked = Ca65Oracle.Pinned.Link(config, [.. generated.Ca65.Select(o => (Path.GetFileName(o.Path), o.Text))]);
        Assert.True(linked.Succeeded, linked.Messages);
        byte[] expected = [.. "NEXT WITHOUT FO"u8, (byte)('R' | 0x80), .. "FO"u8, (byte)('R' | 0x80)];
        Assert.Equal(expected, linked.Binary);
    }

    /// <summary>
    /// The distance nt65 computes between two positions in one data declaration is the distance
    /// at which ca65 lays them out. The same table written by hand, with ca65 subtracting its own
    /// labels, links to the same bytes as nt65's output, which carries only the numbers.
    /// </summary>
    [Fact]
    public void ADistanceInsideADeclarationIsTheOneCa65LaysOut()
    {
        const string Nt65 = """
            .module main
            .segment RODATA
            .data messages {
                .data first: .byte "NEXT", 'X' | $80
                .if 1 {
                    .data second {
                        .byte "SYN", 'T' | $80
                        .word[3]
                    }
                }
                .data third: .word[4]
            }
            .data errors: .byte messages::second - messages, messages::third[2] - messages, .endof(messages) - messages
            """;
        const string ByHand = """
            .setcpu "6502"
            .segment "RODATA": absolute
            messages:
            first: .byte "NEXT", 'X' | $80
            second:
                .byte "SYN", 'T' | $80
                .word 0, 0, 0
            third: .word 0, 0, 0, 0
            messages_end:
                .byte second - messages, third + 4 - messages, messages_end - messages
            """;

        var generated = Compiler.Compile(
            [new SourceFile("main.nt65", Nt65)], ProjectSettings.None with { Cpu = Processor.Cpu.Mos6502 });
        Assert.Empty(generated.Diagnostics);
        var output = Assert.Single(generated.Ca65).Text;
        Assert.Contains(".byte $05, $13, $17", output, StringComparison.Ordinal);

        var linked = LinksLikeByHand(output, ByHand);
        Assert.Equal([5, 19, 23], linked[^3..]);
    }

    /// <summary>
    /// Text built by a <c>.func</c> links to the same bytes as the same text written directly for
    /// ca65. msbasic's <c>htasc</c> sets bit 7 on a message's last byte. Because a function is
    /// evaluated along with the constants, a message's offset in the table is a constant that a
    /// one-byte immediate operand accepts.
    /// </summary>
    [Fact]
    public void TextAFunctionBuildsIsTheTextCa65Writes()
    {
        const string Nt65 = """
            .module main
            .func htasc(text) = .strcat(.strsub(text, 0, .strlen(text) - 1), .strat(text, .strlen(text) - 1) | $80)
            .segment RODATA
            .data messages {
                .data NOFOR: .byte htasc("NEXT WITHOUT FOR")
                .data SYNTAX: .byte htasc("SYNTAX")
            }
            ERR_SYNTAX = messages::SYNTAX - messages
            .data prompt: .strz .strcat(13, ">>", .strsub("?!", 0, 1), 10)
            .segment CODE
            .export .proc start {
                ldx #ERR_SYNTAX
                rts
            }
            """;
        const string ByHand = """
            .setcpu "6502"
            .export main__start
            .segment "RODATA": absolute
            messages:
            NOFOR: .byte "NEXT WITHOUT FO", 'R' | $80
            SYNTAX: .byte "SYNTA", 'X' | $80
            prompt: .byte 13, ">>?", 10, 0
            .segment "CODE": absolute
            main__start:
                ldx #SYNTAX - messages
                rts
            """;

        var generated = Compiler.Compile(
            [new SourceFile("main.nt65", Nt65)], ProjectSettings.None with { Cpu = Processor.Cpu.Mos6502 });
        Assert.Empty(generated.Diagnostics);
        var output = Assert.Single(generated.Ca65).Text;
        Assert.Contains("ERR_SYNTAX = $10", output, StringComparison.Ordinal);

        LinksLikeByHand(output, ByHand);
    }

    [Fact]
    public void RefusesABuildThatIsNotThePinnedCommit()
    {
        var ca65 = Repo.Path(".cache", "cc65", "bin", OperatingSystem.IsWindows() ? "ca65.exe" : "ca65");
        var e = Assert.Throws<InvalidOperationException>(
            () => new Ca65Oracle(ca65, "547d9230000000000000000000000000000000", cacheDirectory: null));
        Assert.Contains("refusing to use ca65", e.Message);
    }

    /// <summary>
    /// Assembles <paramref name="output"/> as <paramref name="fileName"/> and returns a message
    /// for each way ca65 disagrees with it. A disagreement is any message from ca65, or a line
    /// whose byte count is not the one nt65 computed.
    /// </summary>
    internal static IEnumerable<string> AssemblesToComputedLengths(
        string label, OutputFile output, string fileName, IReadOnlyList<(string Name, byte[] Content)> alongside)
    {
        var result = Ca65Oracle.Pinned.Assemble(fileName, output.Text, alongside);
        if (!result.Succeeded)
        {
            yield return $"[{label}] {output.Path}: ca65 reported:\n{result.Messages}";
            yield break;
        }

        var lines = output.Text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        if (output.LineBytes.Count != lines.Length)
        {
            yield return $"[{label}] {output.Path}: {lines.Length} lines, but nt65 has lengths for " +
                $"{output.LineBytes.Count}";
            yield break;
        }
        for (var i = 0; i < lines.Length; i++)
        {
            // A negative length means nt65 makes no claim about the line; ca65's count stands.
            if (output.LineBytes[i] < 0)
                continue;
            if (result.LineBytes[i] != output.LineBytes[i])
            {
                yield return $"[{label}] {output.Path}:{i + 1}: nt65 says {output.LineBytes[i]} bytes, " +
                    $"ca65 generated {result.LineBytes[i]}:\n  {lines[i]}";
            }
        }
    }

    /// <summary>
    /// Returns the linker configuration and the hand-written modules of a fixture, or null if the
    /// fixture has no linker configuration.
    /// </summary>
    private static (string Config, IReadOnlyList<(string Name, string Source)> Modules)? LinkFiles(FixtureCase fixture)
    {
        var directory = Path.Combine(fixture.Directory, "link");
        if (!Directory.Exists(directory))
            return null;
        var config = Path.Combine(directory, "link.cfg");
        if (!File.Exists(config))
            return null;
        return (Repo.ReadText(config), [.. Directory.GetFiles(directory, "*.s")
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), Repo.ReadText(path)))]);
    }

    /// <summary>
    /// Links nt65's <paramref name="output"/> and the same program written by hand,
    /// <paramref name="byHand"/>, against the linker configuration of the <c>modules</c> fixture.
    /// Checks that both link and produce the same bytes, and returns those bytes for the caller to
    /// check further.
    /// </summary>
    private static byte[] LinksLikeByHand(string output, string byHand)
    {
        var config = Repo.ReadText(Repo.Path("tests", "fixtures", "modules", "link", "link.cfg"));
        var fromNt65 = Ca65Oracle.Pinned.Link(config, [("main.s", output)]);
        var fromHand = Ca65Oracle.Pinned.Link(config, [("hand.s", byHand)]);

        Assert.True(fromNt65.Succeeded, fromNt65.Messages);
        Assert.True(fromHand.Succeeded, fromHand.Messages);
        Assert.NotEmpty(fromHand.Binary);
        Assert.Equal(fromHand.Binary, fromNt65.Binary);
        return fromHand.Binary;
    }

    [GeneratedRegex(@";=\s*(\d+)\s*$")]
    private static partial Regex ByteAnnotation();
}
