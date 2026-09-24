using System.Globalization;
using Norristown.Flow;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Analyzes the text of a flow test as <c>main.nt65</c>, compiled after a header that declares
/// the module, chooses the CPU and selects the code segment.
/// </summary>
internal static class FlowFragment
{
    /// <summary>The number of lines the header puts before a test's own text.</summary>
    public const int HeaderLines = 3;

    /// <summary>
    /// Returns the analysis of <paramref name="text"/> for <paramref name="cpu"/>, under the
    /// settings of <see cref="Analysis.Fragment"/>.
    /// </summary>
    public static ProgramAnalysis Analyze(string cpu, string text) =>
        Analysis.Program(Analysis.Fragment, (Analysis.Path, Header(cpu) + text));

    /// <summary>
    /// Returns the problems reported for <paramref name="text"/> on <paramref name="cpu"/>, each
    /// with its line number counted from the start of the test's own text.
    /// </summary>
    public static IReadOnlyList<string> Problems(string cpu, string text) =>
        [.. Analyze(cpu, text).Problems().Select(Renumbered)];

    /// <summary>
    /// Returns a problem, given as <c>file:line: message</c>, with its line number counted from
    /// the start of the test's own text rather than from the header.
    /// </summary>
    public static string Renumbered(string problem)
    {
        var parts = problem.Split(':', 3);
        return $"{parts[0]}:{int.Parse(parts[1], CultureInfo.InvariantCulture) - HeaderLines}:{parts[2]}";
    }

    /// <summary>
    /// Returns the state reaching the first statement of <c>main.nt65</c> whose text is
    /// <paramref name="line"/>.
    /// </summary>
    public static FlowState StateAt(ProgramAnalysis analysis, string line)
    {
        var model = analysis.File(Analysis.Path);
        var statement = model.Tree.Root.DescendantNodes()
            .OfType<LineSyntax>()
            .Select(node => node.Statement)
            .FirstOrDefault(statement => statement.GetText().Trim() == line);
        Assert.True(statement is not null, $"{Analysis.Path} has no statement \"{line}\"");
        var state = analysis.StatesFor(Analysis.Path)?.Before(statement);
        Assert.True(state is not null, $"{Analysis.Path} has no state before \"{line}\"");
        return state;
    }

    /// <summary>Returns the header's text for <paramref name="cpu"/>, which is <see cref="HeaderLines"/> lines long.</summary>
    private static string Header(string cpu) => $".module main\n.cpu {cpu}\n.segment CODE\n";
}
