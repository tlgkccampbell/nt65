using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>Modules: what one file may name in another, and what the output does about it.</summary>
public sealed class ModuleTests
{
    private const string Gfx = """
        .export clear, SCREEN, ptr

        .zeropage {
        ptr:    .res 2
        }

        SCREEN = $0400
        rows   = 25

        .proc clear {
            .export again
        again:
            rts
        }
        """;

    [Fact]
    public void AnExportedNameResolvesToTheFileThatDeclaresIt()
    {
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".proc main {\n    jsr clear\n    rts\n}\n"));

        Assert.Empty(program.Problems());
        var symbol = program.File("main.nt65").SymbolAt("clear");
        Assert.Equal("gfx.nt65", symbol.Tree.Path);
        Assert.Equal(SymbolKind.Proc, symbol.Kind);
    }

    /// <summary>A private name exists; saying so is a better answer than saying it does not.</summary>
    [Fact]
    public void APrivateNameIsReportedAsUnexportedRatherThanUndeclared()
    {
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", "n = rows\n"));

        Assert.Equal(["main.nt65:1: `rows` is declared in `gfx.nt65` and is not exported"], program.Problems());
    }

    /// <summary>
    /// An interior label is exported from inside the routine it belongs to and named
    /// <c>outer::inner</c> from elsewhere; the routine itself need not be exported for that.
    /// </summary>
    [Fact]
    public void AnInteriorLabelIsNamedThroughTheRoutineItIsIn()
    {
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".proc main {\n    jmp clear::again\n}\n"));

        Assert.Empty(program.Problems());
        var symbol = program.File("main.nt65").SymbolAt("again");
        Assert.Equal("clear::again", symbol.QualifiedName);
        Assert.Equal("clear__again", symbol.FlatName);
    }

    /// <summary>
    /// An address becomes an import, sized from its declaration; a constant is written out by
    /// value, because ca65 cannot use an imported symbol where it needs one.
    /// </summary>
    [Fact]
    public void AnAddressIsImportedAndAConstantIsWrittenOut()
    {
        var outputs = Analysis.Outputs(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".proc main {\n    lda ptr\n    lda #<SCREEN\n    jsr clear\n}\n"));

        var main = outputs["main.s"];
        Assert.Contains(".importzp ptr\n", main);
        Assert.Contains(".import clear\n", main);
        Assert.Contains("SCREEN = $0400\n", main);
        Assert.DoesNotContain(".import SCREEN", main);

        // Sized from the segment it is declared in, a file away.
        Assert.Contains("lda z:ptr", main);
    }

    /// <summary>A define is visible everywhere and is never written by name.</summary>
    [Fact]
    public void ADefineIsAConstantInEveryFileAndAValueInTheOutput()
    {
        var project = ProjectSettings.None with
        {
            Defines = [new Define("DEBUG", 2, new Span("nt65.json", 1, 1, 2))],
        };
        var outputs = Analysis.Outputs(project,
            ("one.nt65", ".proc first {\n    lda #DEBUG\n}\n"),
            ("two.nt65", "SIZE = DEBUG * 8\n"));

        Assert.Contains("lda #$02", outputs["one.s"]);
        Assert.Contains("SIZE = $02 * 8", outputs["two.s"]);
    }

    /// <summary>A file may not declare a name the build configuration already gives it.</summary>
    [Fact]
    public void AFileMayNotDeclareADefine()
    {
        var project = ProjectSettings.None with
        {
            Defines = [new Define("DEBUG", 1, new Span("nt65.json", 1, 1, 2))],
        };
        var program = Analysis.Program(project, ("main.nt65", "DEBUG = 2\n"));

        Assert.Equal(
            ["main.nt65:1: `DEBUG` is a define, and a file may not declare one"],
            program.Problems());
    }

    /// <summary>
    /// A checked import is the value nt65 uses and an assertion that what it is linked
    /// against agrees.
    /// </summary>
    [Fact]
    public void ACheckedImportIsUsedByValueAndAsserted()
    {
        var main = Analysis.Outputs(
            ("main.nt65", ".import VIC_BORDER = $d020\n.proc main {\n    sta VIC_BORDER\n}\n"))["main.s"];

        Assert.Contains(".import VIC_BORDER\n", main);
        Assert.Contains(".assert VIC_BORDER = $d020, lderror,", main);
        Assert.Contains("sta a:$d020", main);
    }

    /// <summary>Two files cannot both give the linker the same name.</summary>
    [Fact]
    public void TwoFilesMayNotExportTheSameName()
    {
        var program = Analysis.Program(
            ("a.nt65", ".export thing\nthing = 1\n"),
            ("b.nt65", ".export thing\nthing = 2\n"));

        Assert.Equal(["b.nt65:2: `thing` is exported by two files"], program.Problems());
    }

    /// <summary>
    /// Evaluation is the program's, not the file's, so a constant may be built from one in
    /// another file, and a ring that runs through two files is still reported once.
    /// </summary>
    [Fact]
    public void ConstantsAndTheirCyclesCrossFiles()
    {
        var program = Analysis.Program(
            ("a.nt65", ".export WIDTH\nWIDTH = 40\n"),
            ("b.nt65", "AREA = WIDTH * 25\n"));

        Assert.Empty(program.Problems());
        Assert.Equal(1000, program.File("b.nt65").Symbol("AREA").Value.Number);

        var ring = Analysis.Program(
            ("a.nt65", ".export HERE\nHERE = THERE + 1\n"),
            ("b.nt65", ".export THERE\nTHERE = HERE + 1\n"));

        Assert.Equal(["a.nt65:2: `HERE` is defined in terms of itself"], ring.Problems());
    }

    /// <summary>
    /// An import keeps the spelling it was exported under, because that is the name in the
    /// object file, so a local name that would collide with it is the one that gives way
    ///. Only a generated name can: a fixed spelling that collides is an error.
    /// </summary>
    [Fact]
    public void AGeneratedNameGivesWayToAnImportedOne()
    {
        var outputs = Analysis.Outputs(
            ("gfx.nt65", ".export clear\n.proc clear {\n    rts\n}\n"),
            ("main.nt65", """
                .scope {
                clear:
                    rts
                }

                .proc main {
                    jsr clear
                    rts
                }
                """));

        // Nothing outside an anonymous scope can name what it declares, so its `clear` is a
        // generated name and the import keeps the plain one.
        var main = outputs["main.s"];
        Assert.Contains(".import clear\n", main);
        Assert.Contains("clear_2:", main);
        Assert.Contains("jsr clear\n", main);
    }
}
