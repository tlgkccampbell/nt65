using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents the transfers of control inside the routine that holds the caret, for an editor to
/// draw as arrows. A transfer is a branch, a jump, or an edge a <c>.next</c> declares. A call
/// returns to the line after it and a fall-through runs on into the next line, so neither is a
/// transfer here.
/// <para>
/// The answer is only for showing, and nothing warns or errors because of it. Every arrow in it is
/// an edge the flow analysis already follows, so no transfer the analysis knows about is left out.
/// </para>
/// </summary>
/// <param name="Routine">The span of the routine's text in the caret's file, from the line that opens it to the line that closes it.</param>
/// <param name="Arrows">Each transfer, in the order of the lines that make them.</param>
public sealed record FlowArrows(TextSpan Routine, IReadOnlyList<FlowArrow> Arrows)
{
    /// <summary>
    /// Returns the transfers of control inside the innermost routine of <paramref name="model"/>'s
    /// file whose text holds <paramref name="position"/>. It returns null where no routine opened
    /// in the file holds the position, and where the analysis has no flow for the file.
    /// </summary>
    public static FlowArrows? At(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var tree = model.Tree;
        if (analysis.FileFor(tree.Path) is not { } file || file.Flow.Flags is not { } flags)
            return null;

        FlowRegion? found = null;
        var whole = default(TextSpan);
        foreach (var region in file.Flow.Regions)
        {
            if (Extent(tree, region) is not { } extent || position < extent.Start || position > extent.End)
                continue;
            if (found is null || extent.Length < whole.Length)
                (found, whole) = (region, extent);
        }
        if (found is null)
            return null;

        // A line a macro expands or a repetition unrolls is in the routine once per copy, and each
        // copy makes the same arrow. An arrow is proved only where every copy is, and reached where
        // any copy is.
        var arrows = new Dictionary<(TextSpan From, TextSpan? To, bool Declared), (bool Proved, bool Reached)>();
        foreach (var block in found.Blocks)
        {
            if (block.Steps.Count == 0 || StepLines.Of(tree, block.Steps[^1]) is not var (from, _))
                continue;
            var end = block.Steps[^1];
            var proved = flags.ProvedAt(end) is not null;
            foreach (var edge in block.Successors)
            {
                if (edge.Kind is EdgeKind.Taken or EdgeKind.Declared && LineOf(tree, found.Blocks[edge.To]) is { } to)
                    Add(from, to, edge.Kind == EdgeKind.Declared);
            }

            // A branch proved never taken has lost the edge to its target. The arrow is still
            // drawn, faded, so that the reader sees where the branch would have gone.
            if (flags.ProvedAt(end) is { Taken: false } && Target(model, file, found, end) is var (target, inside))
                Add(from, inside ? target : null, false);

            if (block.End is BlockEnd.TailCall or BlockEnd.Elsewhere || block.BranchesOut
                || (block.End == BlockEnd.Declared && NamesOutside(file.Flow, found, block)))
            {
                Add(from, null, block.Next is not null);
            }

            void Add(TextSpan at, TextSpan? to, bool declared)
            {
                if (to == at)
                    return;
                var key = (at, to, declared);
                arrows[key] = arrows.TryGetValue(key, out var known)
                    ? (known.Proved && proved, known.Reached || block.IsReached)
                    : (proved, block.IsReached);
            }
        }

        return new FlowArrows(
            whole,
            [
                .. arrows
                    .OrderBy(arrow => arrow.Key.From.Start)
                    .ThenBy(arrow => arrow.Key.To?.Start ?? int.MaxValue)
                    .Select(arrow => new FlowArrow(
                        arrow.Key.From, arrow.Key.To, arrow.Key.Declared, arrow.Value.Proved, arrow.Value.Reached)),
            ]);
    }

    /// <summary>
    /// Returns whether the <c>.next</c> under <paramref name="block"/>'s last statement names a
    /// place outside the routine, such as another routine, or says <c>?</c>.
    /// </summary>
    private static bool NamesOutside(ControlFlow flow, FlowRegion region, BasicBlock block) =>
        block.CallsUnknown || block.Calls.Count > 0
        || (block.Next is { } next && flow.Named(next, block.Steps[^1].On)
            .Any(named => !region.Blocks.Any(each => each.Label == named.Symbol)));

    /// <summary>
    /// Returns the span of a routine's text in <paramref name="tree"/>, or null where the routine
    /// is not opened in that file. A routine a block opens runs to the block's closing line. Any
    /// other runs from its opening line to the last of its statements in the file.
    /// </summary>
    private static TextSpan? Extent(SyntaxTree tree, FlowRegion region)
    {
        var routine = region.Routine;
        if (routine.Tree != tree)
            return null;
        var opener = tree.GetLine(tree.GetLineIndex(routine.NameSpan.Start));
        if (opener.Parent is BlockSyntax block && block.Opener == opener)
            return block.Span;
        var end = region.Blocks
            .SelectMany(each => each.Steps)
            .Select(step => StepLines.Of(tree, step)?.Span.End)
            .OfType<int>()
            .Append(opener.Span.End)
            .Max();
        return new TextSpan(opener.Span.Start, end - opener.Span.Start);
    }

    /// <summary>
    /// Returns the span of the line a block starts on in <paramref name="tree"/>, or null where the
    /// block has no line in the file. That is the line of the block's label, where the label is a
    /// line of the file and not part of a macro's body, and the line of its first statement
    /// otherwise.
    /// </summary>
    private static TextSpan? LineOf(SyntaxTree tree, BasicBlock block)
    {
        var first = block.Steps.Count == 0 ? null : StepLines.Of(tree, block.Steps[0]);
        if (block.Label is { } label && label.Tree == tree && first is not { InMacro: true } && (first is not null || block.On is null))
            return label.NameSpan;
        return first?.Span;
    }

    /// <summary>
    /// Returns the line the branch at <paramref name="end"/> would go to and whether that line is in
    /// the routine, or null where nt65 cannot read the branch's target. It is asked about a branch
    /// proved never taken, whose block no longer has the edge.
    /// </summary>
    private static (TextSpan? Line, bool Inside)? Target(SemanticModel model, FileAnalysis file, FlowRegion region, Step end)
    {
        if (end.Statement is not InstructionStatementSyntax statement)
            return null;
        var mode = file.Layout.Of(statement, end.On)?.Mode;
        if (Targets.Of(model, Transfers.TargetOf(statement, mode), end.On) is not { } target)
            return null;
        return region.Blocks.FirstOrDefault(block => block.Label == target.Symbol) is { } landing
            ? (LineOf(model.Tree, landing), true)
            : (null, false);
    }
}
