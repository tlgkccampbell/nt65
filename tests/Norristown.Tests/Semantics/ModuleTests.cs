using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>Modules: what one module may name in another, and what the output does about it.</summary>
public sealed class ModuleTests
{
    private const string Gfx = """
        .module gfx
        .export clear, SCREEN, ptr

        .segment ZEROPAGE
        .data ptr:    .word

        SCREEN = $0400
        rows   = 25

        .segment CODE
        .proc clear {
            .export again
            ldy #rows
        again:
            rts
        }
        """;

    /// <summary>A name from another module is written with the module's path.</summary>
    [Fact]
    public void AQualifiedNameResolvesToTheModuleThatDeclaresIt()
    {
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    jsr gfx::clear\n    rts\n}\n"));

        Assert.Empty(program.Problems());
        var symbol = program.File("main.nt65").SymbolAt("clear");
        Assert.Equal("gfx.nt65", symbol.Tree.Path);
        Assert.Equal(SymbolKind.Proc, symbol.Kind);
    }

    /// <summary>Nothing from another module is visible without its path or a <c>.use</c>, and what does export it is named.</summary>
    [Fact]
    public void AnotherModulesNameIsNotVisibleUnqualified()
    {
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    jsr clear\n    rts\n}\n"));

        Assert.Equal(
            ["main.nt65:4: `clear` is not declared here, and module `gfx` exports it: write `gfx::clear`, "
                + "or bring it in with `.use gfx::clear`"],
            program.Problems());
    }

    /// <summary><c>.use</c> brings names in, one, several or all, and <c>as</c> renames what it brings in.</summary>
    [Theory]
    [InlineData(".use gfx::clear", "clear", "")]
    [InlineData(".use gfx::{SCREEN, clear}", "clear", "    lda SCREEN\n")]
    [InlineData(".use gfx::*", "clear", "")]
    [InlineData(".use gfx::clear as wipe", "wipe", "")]
    [InlineData(".use gfx::{clear as wipe}", "wipe", "")]
    [InlineData(".use gfx as g", "g::clear", "")]
    public void AUseBringsNamesIn(string use, string written, string alsoNamed)
    {
        // Everything a `.use` brings in is named, because a name brought in and never written
        // is reported as an item that may go.
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", $".module main\n{use}\n.segment CODE\n.export .proc main {{\n{alsoNamed}    jsr {written}\n    rts\n}}\n"));

        Assert.Empty(program.Problems());
        var main = program.File("main.nt65");
        var reference = main.ReferenceAt(main.Tree.Text.LastIndexOf(written.Split("::")[^1], StringComparison.Ordinal));
        Assert.Equal("gfx::clear", reference?.Symbol.PathName);
    }

    /// <summary>
    /// A local declaration beats a name a <c>*</c> brought in, so another module adding an export
    /// never changes what a name here means.
    /// </summary>
    [Fact]
    public void ALocalDeclarationBeatsAGlob()
    {
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".module main\n.use gfx::*\nSCREEN = 1\n.export SCREEN\nN = SCREEN\n.export N\n"));

        Assert.Empty(program.Problems());
        Assert.Equal(1, program.File("main.nt65").Symbol("N").Value.Number);
    }

    /// <summary>A private name exists; saying so is a better answer than saying it does not.</summary>
    [Fact]
    public void APrivateNameIsReportedAsUnexportedRatherThanUndeclared()
    {
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".module main\nn = gfx::rows\n"));

        Assert.Equal(["main.nt65:2: `gfx::rows` is not exported by module `gfx`"], program.Problems());
    }

    /// <summary>
    /// An interior label is exported from inside the routine it belongs to and named through the
    /// routine from elsewhere; the routine itself need not be exported for that. Its linker name
    /// has its module's in front.
    /// </summary>
    [Fact]
    public void AnInteriorLabelIsNamedThroughTheRoutineItIsIn()
    {
        var program = Analysis.Program(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    jmp gfx::clear::again\n}\n"));

        Assert.Empty(program.Problems());
        var symbol = program.File("main.nt65").SymbolAt("again");
        Assert.Equal("clear::again", symbol.QualifiedName);
        Assert.Equal("gfx::clear::again", symbol.PathName);
        Assert.Equal("gfx__clear__again", symbol.LinkerName);
    }

    /// <summary>
    /// An address becomes an import under its linker name, sized from its declaration; a
    /// constant is written out by value, because ca65 cannot use an imported symbol where it
    /// needs one.
    /// </summary>
    [Fact]
    public void AnAddressIsImportedAndAConstantIsWrittenOut()
    {
        var outputs = Analysis.Outputs(
            ("gfx.nt65", Gfx),
            ("main.nt65", ".module main\n.use gfx::*\n.segment CODE\n.export .proc main {\n    lda ptr\n    lda #<SCREEN\n    jsr clear\n}\n"));

        var main = outputs["main.s"];
        Assert.Contains(".importzp gfx__ptr\n", main);
        Assert.Contains(".import gfx__clear: abs\n", main);
        Assert.Contains("gfx__SCREEN = $0400\n", main);
        Assert.DoesNotContain(".import gfx__SCREEN", main);
        Assert.Contains(".export gfx__clear\n", outputs["gfx.s"]);

        // Sized from the segment it is declared in, a module away.
        Assert.Contains("lda z:gfx__ptr", main);
    }

    /// <summary>An export may be wider than its declaration, and another module sizes its uses by the export.</summary>
    [Fact]
    public void AnExportSizeWidensWhatOtherModulesSee()
    {
        var outputs = Analysis.Outputs(
            ("vars.nt65", ".module vars\n.export count: abs\n.segment ZEROPAGE\n.data count: .byte\n"),
            ("main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    lda vars::count\n    rts\n}\n"));

        Assert.Contains(".export vars__count: abs\n", outputs["vars.s"]);
        Assert.Contains(".import vars__count: abs\n", outputs["main.s"]);
        Assert.Contains("lda a:vars__count", outputs["main.s"]);
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
            ("one.nt65", ".module one\n.segment CODE\n.proc first {\n    lda #DEBUG\n}\n"),
            ("two.nt65", ".module two\nSIZE = DEBUG * 8\n"));

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
        var program = Analysis.Program(project, ("main.nt65", ".module main\nDEBUG = 2\n"));

        Assert.Equal(
            ["main.nt65:2: `DEBUG` is a define, and a file may not declare one"],
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
            ("main.nt65", ".module main\n.import VIC_BORDER = $d020\n.segment CODE\n.export .proc main {\n    sta VIC_BORDER\n}\n"))["main.s"];

        Assert.Contains(".import VIC_BORDER: abs\n", main);
        Assert.Contains(".assert VIC_BORDER = $d020, lderror,", main);
        Assert.Contains("sta a:$d020", main);
    }

    /// <summary>
    /// A re-exported import is used by other modules as if they had declared it: each imports it
    /// under its own name and asserts its value, and the module that declares it and uses it nowhere
    /// writes nothing for it.
    /// </summary>
    [Fact]
    public void AReexportedImportIsImportedWhereItIsUsed()
    {
        var outputs = Analysis.Outputs(
            ("hw.nt65", ".module hw\n.export .import BORDER = $d020, tick: zp\n"),
            ("main.nt65", ".module main\n.use hw::*\n.segment CODE\n.export .proc main {\n    sta BORDER\n    lda tick\n    rts\n}\n"));

        Assert.Contains(".import BORDER: abs\n", outputs["main.s"]);
        Assert.Contains(".importzp tick\n", outputs["main.s"]);
        Assert.Contains(".assert BORDER = $d020, lderror,", outputs["main.s"]);
        Assert.DoesNotContain(".import", outputs["hw.s"]);
        Assert.DoesNotContain(".export", outputs["hw.s"]);
    }

    /// <summary>
    /// Only what a file uses is imported, since an import pulls its module out of a library: a
    /// path uses what it leads to and not the routine it walks through, a macro body is used
    /// where it is called, and an import nothing uses is not written at all.
    /// </summary>
    [Fact]
    public void OnlyWhatAFileUsesIsImported()
    {
        var outputs = Analysis.Outputs(
            ("lib.nt65", ".module lib\n.segment CODE\n.export .proc outer {\n    .export inner\ninner:\n    rts\n}\n"
                + ".export .proc other {\n    rts\n}\n"),
            ("main.nt65", ".module main\n.import unused: proc(), later: proc()\n.macro call_other() {\n    jsr lib::other\n    jsr later\n}\n"
                + ".segment CODE\n.export .proc main {\n    jmp lib::outer::inner\n}\n"));

        Assert.Contains(".import lib__outer__inner: abs\n", outputs["main.s"]);
        Assert.DoesNotContain("lib__outer\n", outputs["main.s"]);
        Assert.DoesNotContain("lib__other", outputs["main.s"]);
        Assert.DoesNotContain("unused", outputs["main.s"]);
        Assert.DoesNotContain("later", outputs["main.s"]);
    }

    /// <summary>Two modules may each export a name; the linker sees each under its own module's.</summary>
    [Fact]
    public void TwoModulesMayExportTheSameName()
    {
        var outputs = Analysis.Outputs(
            ("gfx.nt65", ".module gfx\n.segment CODE\n.export .proc init {\n    rts\n}\n"),
            ("snd.nt65", ".module snd\n.segment CODE\n.export .proc init {\n    rts\n}\n"),
            ("main.nt65", ".module main\n.use snd::init as snd_init\n.segment CODE\n.export .proc main {\n    jsr gfx::init\n    jsr snd_init\n    rts\n}\n"));

        Assert.Contains("jsr gfx__init\n", outputs["main.s"]);
        Assert.Contains("jsr snd__init\n", outputs["main.s"]);
    }

    /// <summary>Two exports the linker would see under one name are an error, at the export that collides.</summary>
    [Fact]
    public void TwoLinkerNamesThatCollideAreReported()
    {
        var program = Analysis.Program(
            ("a.nt65", ".module first\n.export thing as \"_thing\"\nthing = 1\n"),
            ("b.nt65", ".module second\n.export other as \"_thing\"\nother = 2\n"));

        Assert.Equal(["b.nt65:2: `second::other` and `first::thing` are both exported to the linker as `_thing`"],
            program.Problems());
    }

    /// <summary>
    /// Evaluation is the program's, not the file's, so a constant may be built from one in
    /// another module, and a ring that runs through two modules is still reported once.
    /// </summary>
    [Fact]
    public void ConstantsAndTheirCyclesCrossModules()
    {
        var program = Analysis.Program(
            ("a.nt65", ".module first\n.export WIDTH\nWIDTH = 40\n"),
            ("b.nt65", ".module second\n.export AREA\nAREA = first::WIDTH * 25\n"));

        Assert.Empty(program.Problems());
        Assert.Equal(1000, program.File("b.nt65").Symbol("AREA").Value.Number);

        var ring = Analysis.Program(
            ("a.nt65", ".module first\n.export HERE\nHERE = second::THERE + 1\n"),
            ("b.nt65", ".module second\n.export THERE\nTHERE = first::HERE + 1\n"));

        Assert.Equal(["a.nt65:3: `HERE` is defined in terms of itself"], ring.Problems());
    }

    /// <summary>
    /// An import keeps the spelling it was exported under, because that is the name in the
    /// object file, so a local name that would collide with it is the one that gives way.
    /// Only a generated name can: a fixed spelling that collides is an error.
    /// </summary>
    [Fact]
    public void AGeneratedNameGivesWayToAnImportedOne()
    {
        var outputs = Analysis.Outputs(
            ("gfx.nt65", ".module gfx\n.export clear\n.segment CODE\n.proc clear {\n    rts\n}\n"),
            ("main.nt65", """
                .module main
                .segment CODE
                .scope {
                .proc gfx__clear {
                    rts
                }
                }

                .export .proc main {
                    jsr gfx::clear
                    rts
                }
                """));

        // Nothing outside an anonymous scope can name what it declares, so its `gfx__clear` is a
        // generated name and the import keeps the plain one.
        var main = outputs["main.s"];
        Assert.Contains(".import gfx__clear: abs\n", main);
        Assert.Contains("gfx__clear_2:", main);
        Assert.Contains("jsr gfx__clear\n", main);
    }

    /// <summary>
    /// A mnemonic names a symbol on every CPU, and warns on every CPU: the word reads as an
    /// instruction whoever reads it next, so the warning says the same thing in a 6502
    /// program as in a 65816 one. It names the program's own CPU where the word is an
    /// instruction there, and the first CPU that has it otherwise.
    /// </summary>
    [Fact]
    public void AMnemonicIsANameWithTheSameWarningOnEveryCpu()
    {
        var program = Analysis.Program(
            ("main.nt65", ".module main\n.cpu 6502\nREP = 1\n.export REP\n.segment ZEROPAGE\n.export .data per: .byte\n"));

        Assert.Equal(
            [
                "main.nt65:3: `REP` is an instruction on the 65816; as a name it is legal and easy to misread",
                "main.nt65:6: `per` is an instruction on the 65816; as a name it is legal and easy to misread",
            ],
            program.Problems());
        Assert.Equal(
            ["main.nt65:3: `REP` is an instruction on the 65816; as a name it is legal and easy to misread"],
            Analysis.Program(("main.nt65", ".module main\n.cpu 65816\nREP = 1\n.export REP\n")).Problems());
    }

    /// <summary>
    /// A name another module writes without this one exporting it is reported where it is
    /// written and nowhere else. Something does name the declaration, wrongly, so saying here
    /// that nothing does would be one mistake said twice, with each fix undoing the other.
    /// Nothing naming it is what the warning is for, and taking the use away brings it back.
    /// </summary>
    [Fact]
    public void ANameAnotherModuleWritesWithoutTheExportIsNotAlsoReportedUnused()
    {
        const string Lib = ".module lib\nhidden = 2\n";
        const string Writes = ".module main\n.segment CODE\n.export .proc main {\n    lda #lib::hidden\n    rts\n}\n";
        const string Leaves = ".module main\n.segment CODE\n.export .proc main {\n    lda #1\n    rts\n}\n";

        Assert.Equal(
            ["main.nt65:4: `lib::hidden` is not exported by module `lib`"],
            Analysis.Program(("lib.nt65", Lib), ("main.nt65", Writes)).Problems());
        Assert.Equal(
            ["lib.nt65:2: `hidden` is never used: nothing names it, and it is not exported"],
            Analysis.Program(("lib.nt65", Lib), ("main.nt65", Leaves)).Problems());
    }
}
