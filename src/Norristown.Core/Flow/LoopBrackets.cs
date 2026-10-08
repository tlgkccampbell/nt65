using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents the loops of one routine, for an editor to draw as brackets in front of its lines.
/// A loop is found from a back edge, as the cycle counts find it. Loops that share a header are
/// one loop, with a latch for each back edge.
/// <para>
/// The answer is only for showing, and nothing warns or errors because of it. A loop whose blocks
/// can be entered other than through its header has no back edge and is left out, which draws
/// nothing wrong.
/// </para>
/// </summary>
/// <param name="Routine">The span of the routine's text in the file, from the line that opens it to the line that closes it.</param>
/// <param name="Loops">Each loop, in the order of their first lines, and the outer one first where two start on one line.</param>
public sealed record LoopBrackets(TextSpan Routine, IReadOnlyList<LoopBracket> Loops)
{
    /// <summary>
    /// Returns the loops of every routine opened in <paramref name="model"/>'s file that has any,
    /// in the order the routines are analyzed. It returns an empty list where the analysis has no
    /// flow for the file.
    /// </summary>
    public static IReadOnlyList<LoopBrackets> In(ProgramAnalysis analysis, SemanticModel model)
    {
        var tree = model.Tree;
        var found = new List<LoopBrackets>();
        if (analysis.FileFor(tree.Path) is not { } file)
            return found;
        foreach (var region in file.Flow.Regions)
        {
            if (FlowArrows.Extent(tree, region) is not { } routine)
                continue;

            // A line a macro expands or a repetition unrolls is in the routine once per copy, and
            // each copy makes the same loop. Its count is known only where every copy agrees.
            var loops = new Dictionary<(TextSpan Top, TextSpan Bottom, TextSpan Header), (List<TextSpan> Latches, int? Trips)>();
            foreach (var group in Flow.Loops.In(region.Blocks).GroupBy(loop => loop.Header))
            {
                if (Of(tree, region.Blocks, [.. group]) is not { } bracket)
                    continue;
                var key = (bracket.Top, bracket.Bottom, bracket.Header);
                if (loops.TryGetValue(key, out var known))
                {
                    known.Latches.AddRange(bracket.Latches);
                    loops[key] = (known.Latches, known.Trips == bracket.Trips ? known.Trips : null);
                }
                else
                {
                    loops[key] = ([.. bracket.Latches], bracket.Trips);
                }
            }
            if (loops.Count == 0)
                continue;
            found.Add(new LoopBrackets(
                routine,
                [
                    .. loops
                        .OrderBy(loop => loop.Key.Top.Start)
                        .ThenByDescending(loop => loop.Key.Bottom.Start)
                        .Select(loop => new LoopBracket(
                            loop.Key.Top, loop.Key.Bottom, loop.Key.Header,
                            [.. loop.Value.Latches.Distinct().OrderBy(line => line.Start)], loop.Value.Trips)),
                ]));
        }
        return found;
    }

    /// <summary>
    /// Returns the bracket for the back edges to one header, or null where the header or none of
    /// the latches is on a line of the file. A count is given only where the header has one back
    /// edge and the cycle counts found it a counted loop.
    /// </summary>
    private static LoopBracket? Of(SyntaxTree tree, IReadOnlyList<BasicBlock> blocks, IReadOnlyList<Loop> loops)
    {
        var header = loops[0].Header;
        if (FlowArrows.LineOf(tree, blocks[header]) is not { } head)
            return null;
        var (top, bottom) = (head, head);
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loops.Any(loop => loop.Inside[i]))
                continue;
            var lines = blocks[i].Steps
                .Select(step => StepLines.Of(tree, step)?.Span)
                .Append(FlowArrows.LineOf(tree, blocks[i]))
                .OfType<TextSpan>();
            foreach (var line in lines)
            {
                if (line.Start < top.Start)
                    top = line;
                if (line.Start > bottom.Start)
                    bottom = line;
            }
        }
        var latches = loops
            .Select(loop => blocks[loop.Latch].Steps.Count == 0 ? null : StepLines.Of(tree, blocks[loop.Latch].Steps[^1])?.Span)
            .OfType<TextSpan>()
            .Distinct()
            .OrderBy(line => line.Start)
            .ToList();
        if (latches.Count == 0)
            return null;
        var trips = loops.Count == 1 && blocks[loops[0].Latch].Repeats == header ? blocks[loops[0].Latch].Trips : null;
        return new LoopBracket(top, bottom, head, latches, trips);
    }
}
