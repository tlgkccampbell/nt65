namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks what a name would mean at a position, asked of the model directly rather than found by
/// binding a name already in the source there. An editor asks this about a line being typed, and
/// the answers must be the ones the line will get once it is complete. The binder and the model
/// run the same lookup, and these are the programs where two separate lookups could have
/// differed.
/// </summary>
public sealed class LookupTests
{
    /// <summary>
    /// When a module and a name that a <c>.use module::*</c> brings in are spelled the same, a
    /// path through that name is the module's, because a module is what a path starts at.
    /// </summary>
    [Fact]
    public void AModulePathBeatsWhatAGlobBringsIn()
    {
        var analysis = Analysis.Program(
            ("hw.nt65", ".module hw\n.export BORDER\nBORDER = $d020\n"),
            ("other.nt65", ".module other\n.export hw\nhw = 5\n"),
            ("main.nt65", ".module main\n.use other::*\n.segment CODE\n.proc main {\n    lda hw::BORDER\n    rts\n}\n"));
        var model = analysis.File("main.nt65");
        var position = model.Offset("hw::BORDER");

        var bound = model.SymbolAt("BORDER");
        Assert.Equal("hw", bound.Module);
        Assert.Equal(bound, model.GetSymbolInfo(position, ["hw", "BORDER"]).Symbol);

        // On its own, `hw` is the constant. A module is not a value, so it means the name the `*`
        // brought in. Whether a part of a path means the module depends on the part after it.
        Assert.Equal("other", model.GetSymbolInfo(position, ["hw"]).Symbol?.Module);
    }

    /// <summary>
    /// A lookup at a position tries every name in order, innermost scope first, so a name
    /// declared twice over is answered by the nearer of them.
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

    /// <summary>
    /// A name brought in under an alias with <c>.use ... as</c> is offered and resolved under the
    /// alias only.
    /// </summary>
    [Fact]
    public void AUseAsNameStandsForWhatItBroughtIn()
    {
        var analysis = Analysis.Program(
            ("other.nt65", ".module other\n.export SCREEN\nSCREEN = $0400\n"),
            ("main.nt65", ".module main\n.use other::SCREEN as VRAM\n.segment CODE\n.proc main {\n    lda VRAM\n    rts\n}\n"));
        var model = analysis.File("main.nt65");
        var position = model.Offset("lda VRAM") + 4;

        var bound = model.SymbolAt("VRAM");
        Assert.Equal("SCREEN", bound.Name);
        Assert.Equal(bound, model.GetSymbolInfo(position, ["VRAM"]).Symbol);
        Assert.Contains(model.LookupNames(position), found => found.Name == "VRAM" && found.Means.Symbol == bound);
        Assert.DoesNotContain(model.LookupNames(position), found => found.Name == "SCREEN");
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
        var position = model.Offset(".data here");

        Assert.Equal(model.Symbol("y"), model.GetSymbolInfo(position, ["Point", "y"]).Symbol);
        Assert.Equal(model.Symbol("x"), model.GetSymbolInfo(position, ["here", "x"]).Symbol);
        Assert.True(model.GetSymbolInfo(position, ["here", "z"]).IsNone);
    }

    /// <summary>
    /// A misspelled name in another module gets the same nearest-name suggestion as a misspelled
    /// name in this file, and the same fix puts it right.
    /// </summary>
    [Fact]
    public void AMisspelledNameInAnotherModuleIsGivenTheNearestName()
    {
        var analysis = Analysis.Program(
            ("other.nt65", ".module other\n.export SCREEN\nSCREEN = $0400\n"),
            ("main.nt65", ".module main\n.segment CODE\n.proc main {\n    lda other::SCREN\n    rts\n}\n"));

        var problem = Assert.Single(analysis.File("main.nt65").Diagnostics);
        Assert.Equal("`SCREN` is not declared in module `other`; did you mean `SCREEN`?", problem.Message);
        Assert.Equal(new DiagnosticFix(FixKind.NearestName, "SCREEN"), problem.Fix);
    }

    /// <summary>
    /// A path in a <c>.use</c> is walked as the same path in an expression is, so a misspelled
    /// member gets the same message and the same nearest-name fix in both.
    /// </summary>
    [Fact]
    public void AUsePathIsReportedAsTheSamePathInAnExpressionIs()
    {
        var analysis = Analysis.Program(
            ("other.nt65", ".module other\n.export .scope regs {\n    BORDER = $d020\n}\n"),
            ("main.nt65", ".module main\n.use other::regs::BORDR\n.segment CODE\n.proc main {\n    lda other::regs::BORDR\n    rts\n}\n"));

        var problems = analysis.File("main.nt65").Diagnostics;
        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem =>
        {
            Assert.Equal("`BORDR` is not declared in `regs`; did you mean `BORDER`?", problem.Message);
            Assert.Equal(new DiagnosticFix(FixKind.NearestName, "BORDER"), problem.Fix);
        });
    }

    /// <summary>
    /// Every reference to a name is found, in every file, and those are what a rename replaces.
    /// </summary>
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
