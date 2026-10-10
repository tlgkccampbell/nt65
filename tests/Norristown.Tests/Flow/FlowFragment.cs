using System.Globalization;
using Norristown.Flow;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Analyzes the text of a flow test as <c>main.nt65</c>, compiled after a header that declares
/// the module, chooses the CPU and selects the code segment, and reads the answers of the flow
/// analyses back in the form a test asks about them.
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
        Analyze(Analysis.Fragment, cpu, (Analysis.Path, text));

    /// <summary>
    /// Returns the analysis of <paramref name="files"/>, each given as its path and text, for
    /// <paramref name="cpu"/> under <paramref name="project"/>. Each file is compiled after the
    /// header, with the module named after the file.
    /// </summary>
    public static ProgramAnalysis Analyze(ProjectSettings project, string cpu, params (string Path, string Text)[] files) =>
        Analysis.Program(project, [.. files.Select(file => (file.Path, Header(cpu, Path.GetFileNameWithoutExtension(file.Path)) + file.Text))]);

    /// <summary>
    /// Returns the problems reported for <paramref name="text"/> on <paramref name="cpu"/>, each
    /// with its line number counted from the start of the test's own text.
    /// </summary>
    public static IReadOnlyList<string> Problems(string cpu, string text) =>
        [.. Analyze(cpu, text).Problems().Select(Renumbered)];

    /// <summary>
    /// Returns the ca65 source written for <paramref name="text"/> on <paramref name="cpu"/>,
    /// compiled after the header with no project settings.
    /// </summary>
    public static string Output(string cpu, string text) =>
        Analysis.Outputs((Analysis.Path, Header(cpu) + text))["main.s"];

    /// <summary>
    /// Returns a problem, given as <c>file:line: message</c>, with its line number counted from
    /// the start of the test's own text rather than from the header.
    /// </summary>
    public static string Renumbered(string problem)
    {
        var parts = problem.Split(':', 3);
        return $"{parts[0]}:{int.Parse(parts[1], CultureInfo.InvariantCulture) - HeaderLines}:{parts[2]}";
    }

    /// <summary>Returns the flow analysis of <c>main.nt65</c>, which must have one.</summary>
    public static ControlFlow Flow(ProgramAnalysis analysis)
    {
        var flow = analysis.FlowFor(Analysis.Path);
        Assert.NotNull(flow);
        return flow;
    }

    /// <summary>Returns the region of the one routine of <c>main.nt65</c> named <paramref name="routine"/>.</summary>
    public static FlowRegion Region(ProgramAnalysis analysis, string routine) =>
        Flow(analysis).Regions.Single(region => region.Routine.DisplayName == routine);

    /// <summary>Returns what the routine of <c>main.nt65</c> named <paramref name="routine"/> leaves on its caller's stack.</summary>
    public static StackEffect EffectOf(ProgramAnalysis analysis, string routine)
    {
        var flow = Flow(analysis);
        return flow.Effects.Of(flow.Regions.Single(region => region.Routine.DisplayName == routine).Routine);
    }

    /// <summary>
    /// Returns the first statement of <c>main.nt65</c> whose text is <paramref name="line"/>,
    /// which must exist.
    /// </summary>
    public static StatementSyntax Statement(SemanticModel model, string line)
    {
        var statement = model.Tree.Root.DescendantNodes()
            .OfType<LineSyntax>()
            .Select(node => node.Statement)
            .FirstOrDefault(statement => statement.GetText().Trim() == line);
        Assert.True(statement is not null, $"{Analysis.Path} has no statement \"{line}\"");
        return statement;
    }

    /// <summary>
    /// Returns the state reaching the first statement of <c>main.nt65</c> whose text is
    /// <paramref name="line"/>.
    /// </summary>
    public static FlowState StateAt(ProgramAnalysis analysis, string line)
    {
        var statement = Statement(analysis.File(Analysis.Path), line);
        var state = analysis.StatesFor(Analysis.Path)?.Before(statement);
        Assert.True(state is not null, $"{Analysis.Path} has no state before \"{line}\"");
        return state;
    }

    /// <summary>
    /// Returns what the registers hold before the first statement of <c>main.nt65</c> whose text
    /// is <paramref name="line"/>.
    /// </summary>
    public static RegisterState RegistersAt(ProgramAnalysis analysis, string line)
    {
        var statement = Statement(analysis.File(Analysis.Path), line);
        var state = analysis.FlowFor(Analysis.Path)?.Registers?.Before(statement);
        Assert.NotNull(state);
        return state;
    }

    /// <summary>
    /// Returns where each input of the first statement of <c>main.nt65</c> whose text is
    /// <paramref name="line"/> was set, or null where <see cref="InputSources.At"/> has no answer.
    /// Each input is one string: its name, then each source, then each through line after
    /// <c>via</c>. A source is the text of its line, prefixed with its kind unless it is an
    /// instruction, and an entry source is the word <c>entry</c>. Each line that might also have
    /// changed a value in memory follows <c>or possibly</c>, and the reason a best-effort source
    /// gives follows in brackets.
    /// </summary>
    public static IReadOnlyList<string>? SourcesAt(ProgramAnalysis analysis, string line)
    {
        var model = analysis.File(Analysis.Path);
        var statement = Statement(model, line);
        if (InputSources.At(analysis, model, statement.Span.Start) is not { } found)
            return null;
        return [.. found.Inputs.Select(input =>
        {
            var sources = input.Sources.Select(source => source.Kind switch
            {
                SourceKind.Entry => "entry",
                SourceKind.Instruction => Text(model.Tree, source.Line),
                SourceKind.Unknown => $"? {Text(model.Tree, source.Line)} ({source.Reason})",
                _ => $"{source.Kind.ToString().ToLowerInvariant()} {Text(model.Tree, source.Line)}",
            });
            var through = input.Through.Count == 0 ? "" : " via " + string.Join(", ", input.Through.Select(span => Text(model.Tree, span)));
            through += input.Possibly.Count == 0 ? "" : " or possibly " + string.Join(", ", input.Possibly.Select(span => Text(model.Tree, span)));
            var doubted = input.Sources.FirstOrDefault(source => source is { Confidence: SourceConfidence.BestEffort, Reason: not null });
            return $"{input.Name}: {string.Join(", ", sources)}{through}{(doubted is null ? "" : $" [{doubted.Reason}]")}";
        })];

        static string Text(SyntaxTree tree, TextSpan span) => tree.Text[span.Start..span.End].Trim();
    }

    /// <summary>
    /// Returns the text of the line holding <paramref name="span"/>, without its indentation.
    /// </summary>
    public static string LineText(SyntaxTree tree, TextSpan span)
    {
        var line = tree.GetLineIndex(span.Start);
        var start = tree.LineStarts[line];
        var end = line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
        return tree.Text[start..end].Trim();
    }

    /// <summary>Returns the header's text for <paramref name="cpu"/>, which is <see cref="HeaderLines"/> lines long.</summary>
    private static string Header(string cpu, string module = "main") => $".module {module}\n.cpu {cpu}\n.segment CODE\n";
}
