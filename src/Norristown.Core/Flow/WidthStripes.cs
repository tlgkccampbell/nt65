using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents the register widths and the mode on the lines of one 65816 routine, for an editor to
/// draw as stripes beside them. A line's state is the one that reaches its first instruction, so
/// the stripe on a line is the width that sizes the line's own immediate.
/// <para>
/// The answer is only for showing, and nothing warns or errors because of it. A line that holds
/// no step of the routine has no stripe of its own.
/// </para>
/// </summary>
/// <param name="Routine">The span of the routine's text in the file, from the line that opens it to the line that closes it.</param>
/// <param name="Lines">
/// Each line that a step of the routine is shown on, in the order of the lines. A line that
/// nothing reaches has an unknown state.
/// </param>
public sealed record WidthStripes(TextSpan Routine, IReadOnlyList<WidthStripe> Lines)
{
    /// <summary>
    /// Returns the stripes of every routine opened in <paramref name="model"/>'s file, in the
    /// order the routines are analyzed. It returns an empty list where the file has no processor
    /// state, which is so on every processor but the 65816.
    /// </summary>
    public static IReadOnlyList<WidthStripes> In(ProgramAnalysis analysis, SemanticModel model)
    {
        var tree = model.Tree;
        var found = new List<WidthStripes>();
        if (analysis.FileFor(tree.Path) is not { State: { } states } file)
            return found;
        foreach (var region in file.Flow.Regions)
        {
            if (FlowArrows.Extent(tree, region) is not { } routine)
                continue;

            // A line is entered where its first step runs, whether control runs on into it or
            // jumps to it. A macro's call is one line for all the steps of its body, and a line a
            // macro expands or a repetition unrolls is in the routine once per copy, so each line
            // gets what every entry into it agrees on.
            var lines = new Dictionary<TextSpan, ProcessorState>();
            foreach (var block in region.Blocks)
            {
                // A block's label is entered with the state of the block's first step.
                TextSpan? previous = null;
                if (FlowArrows.LineOf(tree, block) is { } label && block.Steps.Count > 0
                    && StepLines.Of(tree, block.Steps[0])?.Span != label)
                {
                    Enter(lines, states, label, block.Steps[0]);
                }
                foreach (var step in block.Steps)
                {
                    var line = StepLines.Of(tree, step)?.Span;
                    if (line is not { } entered || line == previous)
                        continue;
                    previous = line;
                    Enter(lines, states, entered, step);
                }
            }
            if (lines.Count > 0)
            {
                found.Add(new WidthStripes(
                    routine,
                    [.. lines.OrderBy(line => line.Key.Start).Select(line => new WidthStripe(line.Key, line.Value))]));
            }
        }
        return found;
    }

    /// <summary>
    /// Records that <paramref name="line"/> is entered with the state that reaches
    /// <paramref name="step"/>, keeping only what it agrees on with each other entry. A step that
    /// nothing reaches has an unknown state, so that a line of dead code is never drawn as live.
    /// </summary>
    private static void Enter(Dictionary<TextSpan, ProcessorState> lines, StateAnalysis states, TextSpan line, Step step)
    {
        var reaching = states.Before(step.Statement, step.On) is { IsDead: false } state ? state.Processor : ProcessorState.Unknown;
        lines[line] = lines.TryGetValue(line, out var known) ? Agreed(known, reaching) : reaching;
    }

    /// <summary>Returns what two states agree on, with each part on which they differ unknown.</summary>
    private static ProcessorState Agreed(ProcessorState a, ProcessorState b) => new(
        a.A == b.A ? a.A : Width.Unknown,
        a.Index == b.Index ? a.Index : Width.Unknown,
        a.E == b.E ? a.E : ProcessorMode.Unknown,
        StateValue.Merge(a.D, b.D),
        StateValue.Merge(a.B, b.B));
}
