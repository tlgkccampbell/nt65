using Norristown.LanguageServer;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Layout;

/// <summary>
/// The lines of a macro body declared in another file, written out in this one. A position
/// means something only in the file it was written in, so what this file says about such a
/// line is never read off whichever of its own lines sits at the same position.
/// </summary>
public sealed class ForeignMacroLineTests
{
    /// <summary>
    /// A problem with a line of another file's macro body is reported at the call, in this
    /// file, with the body line named beside it.
    /// </summary>
    [Fact]
    public void AProblemWithAnotherFilesBodyLineIsReportedAtTheCall()
    {
        var analysis = Analysis.Program(
            ("defs.nt65", """
                .module defs
                .export peek

                .macro peek() {
                    lda d:$05
                }
                """),
            ("main.nt65", """
                .module main
                .use defs::peek
                .segment CODE
                .proc main {
                    peek!()
                    rts
                }
                """));

        var problem = Assert.Single(analysis.Diagnostics);
        Assert.Equal(new Span("main.nt65", 5, 5, 12), problem.Span);
        Assert.Equal(new Span("defs.nt65", 5, 9, 14), Assert.Single(problem.Related).Span);
    }

    /// <summary>
    /// An instruction is timed as itself, not as the line of another file's macro body that
    /// was laid out first at the same position.
    /// </summary>
    [Fact]
    public void AnInstructionIsTimedAsItselfNotAsAnotherFilesLineAtTheSamePosition()
    {
        var analysis = Analysis.Program(
            ("defs.nt65", ".module defs\n.segment CODE\n.export one\n; ten chars\n.macro one() {\n    lda #1\n}\n"),
            ("main.nt65", ".module main\n.use defs::one\n.segment CODE\n.proc main {\n    one!()\n    sta $10\n    rts\n}\n"));
        var main = analysis.File("main.nt65");
        var sta = main.Offset("sta");
        Assert.Equal(sta, analysis.File("defs.nt65").Offset("lda"));

        var hover = Lsp.ToHover(
            main, analysis.LayoutFor("main.nt65"), analysis.FlowFor("main.nt65"), analysis.StatesFor("main.nt65"), sta);

        Assert.NotNull(hover);
        Assert.StartsWith("**3 cycles**", hover.Contents.Value);
    }
}
