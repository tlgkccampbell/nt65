using Norristown.LanguageServer;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the hover on the name in an <c>.allow</c>, which explains the warning and gives the lines
/// the <c>.allow</c> covers and its reason.
/// </summary>
public sealed class AllowHoverTests
{
    [Fact]
    public void TheNameExplainsTheWarningAndGivesWhatIsCovered()
    {
        const string Text = ".module main\n.cpu 6502\n.segment CODE\n"
            + ".allow \"unused-symbol\", \"the debugger calls it\"\n.proc helper {\n    rts\n}\n";
        var analysis = Analysis.Program((Analysis.Path, Text));
        var model = analysis.File(Analysis.Path);

        var hover = Hovers.At(analysis, model, Text.IndexOf("unused-symbol", StringComparison.Ordinal) + 1);

        Assert.NotNull(hover);
        var text = hover.Contents.Value;
        Assert.Contains("Nothing in the program names the declaration", text, StringComparison.Ordinal);
        Assert.Contains("lines 5-7", text, StringComparison.Ordinal);
        Assert.Contains("the debugger calls it", text, StringComparison.Ordinal);
    }
}
