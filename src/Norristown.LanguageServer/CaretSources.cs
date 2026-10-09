using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the <c>nt65/sources</c> request. It converts what <see cref="InputSources"/>,
/// <see cref="OutputReaders"/> and <see cref="PatchLinks"/> find for the caret into whole-line
/// ranges, which is what the client highlights.
/// </summary>
internal static class CaretSources
{
    /// <summary>
    /// Returns where each input of the instruction on the line at <paramref name="position"/> was
    /// set, where each of its outputs is read and which stores into code the line takes part in.
    /// It returns null where the analysis has no answer about the line.
    /// </summary>
    public static Protocol.SourcesResult? At(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var found = InputSources.At(analysis, model, position);
        var read = OutputReaders.At(analysis, model, position);
        var patches = PatchLinks.At(analysis, model, position);
        if (found is null && read is null && patches is null)
            return null;
        var tree = model.Tree;
        return new Protocol.SourcesResult(
            Line(tree, (found?.Routine ?? read?.Routine ?? patches!.Routine).Start),
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
                    Confidence(reader.Confidence)))]))],
            [.. (patches?.Links ?? []).Select(link => new Protocol.PatchSpan(
                Line(tree, link.Store.Start), Line(tree, link.Target.Start), link.Name, link.Variants))]);
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
