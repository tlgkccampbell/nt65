using Norristown.Project;

namespace Norristown.Tests.Semantics;

/// <summary>
/// What a name means where it is written, asked of the model rather than found by binding a
/// name that is already there. An editor asks these about a line being typed, and the answers
/// have to be the ones the line will get once it is written: the binder and the model run the
/// same lookup, and these are the programs where two lookups could have differed.
/// </summary>
public sealed class LookupTests
{
    /// <summary>A build that gives every file a define, which is what a glob has to lose to.</summary>
    private static ProjectSettings WithDefine => ProjectSettings.None with
    {
        Defines = [new Define("LIMIT", 7, default)],
    };

    /// <summary>
    /// A define and a <c>.use module::*</c> under one name: the define wins, because a module
    /// adding an export may not change what a name already means.
    /// </summary>
    [Fact]
    public void ADefineBeatsWhatAGlobBringsIn()
    {
        var analysis = Analysis.Program(
            WithDefine,
            ("other.nt65", ".module other\n.export LIMIT\nLIMIT = 1\n"),
            ("main.nt65", ".module main\n.use other::*\n.segment CODE\n.proc main {\n    lda #LIMIT\n    rts\n}\n"));
        var model = analysis.File("main.nt65");
        var written = model.Offset("LIMIT");

        var bound = model.SymbolAt("LIMIT");
        Assert.True(bound.IsDefine);
        Assert.Equal(bound, model.GetSymbolInfo(written, ["LIMIT"]).Symbol);

        // The glob's name is a candidate the lookup passed over, and the define is the answer.
        Assert.Equal(bound, model.LookupSymbols(written, "LIMIT")[0]);
        Assert.Contains(model.LookupSymbols(written, "LIMIT"), symbol => symbol.Module == "other");
    }

    /// <summary>
    /// A module and a name a <c>.use module::*</c> brings in, spelled the same: a path through
    /// it is the module's, because a module is what a path starts at.
    /// </summary>
    [Fact]
    public void AModulePathBeatsWhatAGlobBringsIn()
    {
        var analysis = Analysis.Program(
            ("hw.nt65", ".module hw\n.export BORDER\nBORDER = $d020\n"),
            ("other.nt65", ".module other\n.export hw\nhw = 5\n"),
            ("main.nt65", ".module main\n.use other::*\n.segment CODE\n.proc main {\n    lda hw::BORDER\n    rts\n}\n"));
        var model = analysis.File("main.nt65");
        var written = model.Offset("hw::BORDER");

        var bound = model.SymbolAt("BORDER");
        Assert.Equal("hw", bound.Module);
        Assert.Equal(bound, model.GetSymbolInfo(written, ["hw", "BORDER"]).Symbol);

        // Written alone it is the constant: a module is no value, so a name a `*` brought in
        // is what stands there. Which of the two a part means is what the part after it says.
        Assert.Equal("other", model.GetSymbolInfo(written, ["hw"]).Symbol?.Module);
    }

    /// <summary>
    /// Every name a lookup at a position tries, in the order it tries them: the innermost scope
    /// first, so a name declared twice over is answered by the nearer of them.
    /// </summary>
    [Fact]
    public void ANameIsLookedUpFromTheInnermostScopeOutward()
    {
        var model = Analysis.Model("""
            .module main
            COUNT = 1
            .segment CODE
            .proc main {
            COUNT = 2
            @loop:
                lda #COUNT
                bne @loop
                rts
            }
            """);
        var inside = model.Offset("lda #COUNT") + 5;
        var outside = model.Offset("COUNT = 1");

        Assert.Equal(model.SymbolAt("COUNT", 3), model.LookupSymbols(inside, "COUNT")[0]);
        Assert.Equal(2, model.LookupSymbols(inside, "COUNT").Count);
        Assert.Single(model.LookupSymbols(outside, "COUNT"));

        // A cheap local is written with its `@`, which is the name it answers to.
        Assert.Equal(model.Symbol("@loop"), Assert.Single(model.LookupSymbols(inside, "@loop")));
        Assert.Empty(model.LookupSymbols(inside, "loop"));
        Assert.Empty(model.LookupSymbols(outside, "@loop"));
    }

    /// <summary>A name brought in under another is offered, and reached, under the name written here.</summary>
    [Fact]
    public void AUseAsNameStandsForWhatItBroughtIn()
    {
        var analysis = Analysis.Program(
            ("other.nt65", ".module other\n.export SCREEN\nSCREEN = $0400\n"),
            ("main.nt65", ".module main\n.use other::SCREEN as VRAM\n.segment CODE\n.proc main {\n    lda VRAM\n    rts\n}\n"));
        var model = analysis.File("main.nt65");
        var written = model.Offset("lda VRAM") + 4;

        var bound = model.SymbolAt("VRAM");
        Assert.Equal("SCREEN", bound.Name);
        Assert.Equal(bound, model.GetSymbolInfo(written, ["VRAM"]).Symbol);
        Assert.Contains(model.LookupNames(written), found => found.Name == "VRAM" && found.Means.Symbol == bound);
        Assert.DoesNotContain(model.LookupNames(written), found => found.Name == "SCREEN");
    }

    /// <summary>A path into a type's members, which is what a record initializer is completed from.</summary>
    [Fact]
    public void APathWalksIntoWhatATypeNames()
    {
        var model = Analysis.Model("""
            .module main
            .struct Point {
            x:  .word
            y:  .word
            }
            .segment BSS
            .data here: .type Point
            """);
        var written = model.Offset(".data here");

        Assert.Equal(model.Symbol("y"), model.GetSymbolInfo(written, ["Point", "y"]).Symbol);
        Assert.Equal(model.Symbol("x"), model.GetSymbolInfo(written, ["here", "x"]).Symbol);
        Assert.True(model.GetSymbolInfo(written, ["here", "z"]).IsNone);
    }

    /// <summary>
    /// A misspelled name in another module is told what it was nearly, as a misspelled one in
    /// this file is, and the same fix puts it right.
    /// </summary>
    [Fact]
    public void AMisspelledNameInAnotherModuleIsToldWhatItWasNearly()
    {
        var analysis = Analysis.Program(
            ("other.nt65", ".module other\n.export SCREEN\nSCREEN = $0400\n"),
            ("main.nt65", ".module main\n.segment CODE\n.proc main {\n    lda other::SCREN\n    rts\n}\n"));

        var problem = Assert.Single(analysis.File("main.nt65").Diagnostics);
        Assert.Equal("`SCREN` is not declared in module `other`; `SCREEN` is", problem.Message);
        Assert.Equal(new DiagnosticFix(FixKind.NearestName, "SCREEN"), problem.Fix);
    }

    /// <summary>Every place a name is written, in every file, which is what a rename writes over.</summary>
    [Fact]
    public void ReferencesToANameSpanTheProgram()
    {
        var analysis = Analysis.Program(
            ("other.nt65", ".module other\n.export SCREEN\nSCREEN = $0400\n"),
            ("main.nt65", ".module main\n.use other::SCREEN\n.segment CODE\n.proc main {\n    lda SCREEN\n    sta SCREEN\n    rts\n}\n"));
        var screen = analysis.File("other.nt65").Symbol("SCREEN");

        Assert.Equal(
            ["main.nt65", "main.nt65", "main.nt65", "other.nt65", "other.nt65"],
            analysis.Program.ReferencesTo(screen).Select(found => found.File.Tree.Path));

        // The declaration is among them, and it is the one in the file that declares it.
        var declaration = Assert.Single(analysis.Program.ReferencesTo(screen), found => found.Reference.IsDeclaration);
        Assert.Equal("other.nt65", declaration.File.Tree.Path);
    }
}
