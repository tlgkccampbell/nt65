using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the <c>nt65/sources</c> request. It converts what <see cref="InputSources"/> finds for
/// the caret, and what <see cref="OutputReaders"/> finds, into whole-line ranges, which is what the
/// client highlights.
/// </summary>
internal static class CaretSources
{
    /// <summary>
    /// Returns where each input of the instruction on the line at <paramref name="position"/> was
    /// set and where each of its outputs is read, or null where there is no instruction there or
    /// the analysis has no answer.
    /// </summary>
    public static Protocol.SourcesResult? At(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var found = InputSources.At(analysis, model, position);
        var read = OutputReaders.At(analysis, model, position);
        if (found is null && read is null)
            return null;
        var tree = model.Tree;
        return new Protocol.SourcesResult(
            Line(tree, (found?.Routine ?? read!.Routine).Start),
            [.. (found?.Inputs ?? []).Select(input => new Protocol.SourcesInput(
                input.Name,
                input.Group,
                input.Category.ToString().ToLowerInvariant(),
                [.. input.Sources.Select(source => new Protocol.SourceSpan(
                    Line(tree, source.Line.Start),
                    source.Kind.ToString().ToLowerInvariant(),
                    Confidence(source.Confidence),
                    source.Blocker is { } blocker ? Line(tree, blocker.Start) : null,
                    source.Reason))],
                [.. input.Through.Select(span => Line(tree, span.Start))],
                [.. input.Possibly.Select(span => Line(tree, span.Start))]))],
            [.. (read?.Outputs ?? []).Select(output => new Protocol.SourcesOutput(
                output.Name,
                output.Group,
                output.Category.ToString().ToLowerInvariant(),
                [.. output.Readers.Select(reader => new Protocol.ReaderSpan(
                    Line(tree, reader.Line.Start),
                    reader.Kind.ToString().ToLowerInvariant(),
                    Confidence(reader.Confidence)))]))]);
    }

    /// <summary>Returns how a confidence is written in the protocol.</summary>
    private static string Confidence(SourceConfidence confidence) =>
        confidence == SourceConfidence.Proven ? "proven" : "bestEffort";

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
