using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the <c>nt65/sources</c> request. It converts what <see cref="InputSources"/> finds for
/// the caret into whole-line ranges, which is what the client highlights.
/// </summary>
internal static class CaretSources
{
    /// <summary>
    /// Returns where each input of the instruction on the line at <paramref name="position"/> was
    /// set, or null where there is no instruction there or the analysis has no answer.
    /// </summary>
    public static Protocol.SourcesResult? At(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        if (InputSources.At(analysis, model, position) is not { } found)
            return null;
        var tree = model.Tree;
        return new Protocol.SourcesResult(
            Line(tree, found.Routine.Start),
            [.. found.Inputs.Select(input => new Protocol.SourcesInput(
                input.Name,
                input.Group,
                input.Category.ToString().ToLowerInvariant(),
                [.. input.Sources.Select(source => new Protocol.SourceSpan(
                    Line(tree, source.Line.Start),
                    source.Kind.ToString().ToLowerInvariant(),
                    source.Confidence == SourceConfidence.Proven ? "proven" : "bestEffort",
                    source.Blocker is { } blocker ? Line(tree, blocker.Start) : null,
                    source.Reason))],
                [.. input.Through.Select(span => Line(tree, span.Start))],
                [.. input.Possibly.Select(span => Line(tree, span.Start))]))]);
    }

    /// <summary>
    /// Returns the range of the whole line that holds <paramref name="position"/>, without its line
    /// break. A client that highlights whole lines would otherwise also highlight the next one.
    /// </summary>
    private static Protocol.Range Line(SyntaxTree tree, int position)
    {
        var line = tree.GetLineIndex(position);
        var start = tree.LineStarts[line];
        var end = line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
        while (end > start && tree.Text[end - 1] is '\n' or '\r')
            end--;
        return new Protocol.Range(new Protocol.Position(line, 0), new Protocol.Position(line, end - start));
    }
}
