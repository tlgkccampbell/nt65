using Norristown.Tests.Flow;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks <c>.allow</c>, which keeps the warning it names from being reported on the statement
/// below it, and reports itself when it hides nothing.
/// </summary>
public sealed class AllowTests
{
    private const string Unused = ".proc helper {\n    rts\n}\n";

    // The fragments of the flow tests turn `unused-symbol` off, and these tests need it on.
    private const string Header = ".module main\n.cpu 6502\n.segment CODE\n";

    /// <summary>The warning is reported without the <c>.allow</c> and hidden with it, reason or not.</summary>
    [Theory]
    [InlineData(".allow \"unused-symbol\"\n")]
    [InlineData(".allow \"unused-symbol\", \"the debugger calls it\"\n")]
    public void AnAllowHidesTheWarningItNames(string allow)
    {
        Assert.Equal(["main.nt65:1: `helper` is never used or exported"],
            Problems(Unused));
        Assert.Empty(Problems(allow + Unused));
    }

    /// <summary>
    /// Before a line that opens a block, an <c>.allow</c> covers the whole block, and it covers
    /// nothing after the statement below it.
    /// </summary>
    [Fact]
    public void AnAllowCoversTheStatementBelowAndItsBlock()
    {
        Assert.Empty(Problems(
            ".export p\n.allow \"label-unreachable\"\n.proc p {\n    rts\n@dead:\n    rts\n}\n"));
        Assert.Equal(["main.nt65:5: `other` is never used or exported"],
            Problems(".allow \"unused-symbol\"\n" + Unused + ".proc other {\n    rts\n}\n"));
    }

    /// <summary>
    /// An <c>.allow</c> that hides nothing is reported, with a fix that removes it, unless a macro
    /// body or a branch the build leaves out holds it.
    /// </summary>
    [Fact]
    public void AnAllowThatHidesNothingIsReported()
    {
        var analysis = Analyze(".export helper\n.allow \"unused-symbol\"\n" + Unused);
        var stale = Assert.Single(analysis.Diagnostics);
        Assert.Equal("allow-unused", stale.Id);
        Assert.Equal("`.allow \"unused-symbol\"` hides nothing: the block below it has no such warning", stale.Message);
        Assert.Equal(FixKind.Redundant, stale.Fix?.Kind);
        Assert.True(stale.IsUnnecessary);

        Assert.Empty(Problems(
            ".export helper\n.if 0 {\n    .allow \"unused-symbol\"\n    .const two = 2\n}\n" + Unused));
        Assert.Empty(Problems(
            ".export p\n.macro m() {\n    .allow \"unused-symbol\"\n    nop\n}\n.proc p {\n    m!()\n    rts\n}\n"));
    }

    /// <summary>
    /// A name nt65 does not report, an error, and a diagnostic an annotation answers are each
    /// refused, as is an <c>.allow</c> with nothing below it.
    /// </summary>
    [Theory]
    [InlineData(".allow \"unused-symbl\"\n" + Unused, "main.nt65:1: `unused-symbl` is not a diagnostic nt65 reports; did you mean `unused-symbol`?")]
    [InlineData(".allow \"not-declared\"\n" + Unused, "main.nt65:1: `not-declared` is an error, and `.allow` hides only warnings")]
    [InlineData(".allow \"runs-into-data\"\n" + Unused,
        "main.nt65:1: `.allow` cannot hide `runs-into-data`: add the annotation its message names, which tells nt65 what really happens")]
    [InlineData(".proc helper {\n    rts\n    .allow \"unused-symbol\"\n}\n", "main.nt65:3: `.allow` applies to the statement below it, and there is none")]
    public void AnAllowIsRefused(string text, string problem) =>
        Assert.Contains(problem, Problems(text));

    /// <summary>A misspelt name offers the nearest name, in quotes.</summary>
    [Fact]
    public void AMisspeltNameOffersTheNearest()
    {
        var diagnostic = Analyze(".allow \"unused-symbl\"\n" + Unused).Diagnostics
            .Single(d => d.Id == "diagnostic-name-unknown");
        Assert.Equal("\"unused-symbol\"", diagnostic.Fix?.Text);
    }

    /// <summary>Returns the analysis of <paramref name="text"/> after the header, with no project settings.</summary>
    private static ProgramAnalysis Analyze(string text) => Analysis.Program((Analysis.Path, Header + text));

    /// <summary>Returns the problems reported for <paramref name="text"/>, numbered from its first line.</summary>
    private static IReadOnlyList<string> Problems(string text) =>
        [.. Analyze(text).Problems().Select(FlowFragment.Renumbered)];
}
