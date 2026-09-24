using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Decides which branch of each <c>.if</c> chain in a run of siblings is included. A chain is a
/// run of sibling blocks, made up of the <c>.if</c> that starts it followed by any
/// <c>.elseif</c> and <c>.else</c> blocks that continue it. Every pass that walks siblings reads
/// the chains here, so a line and a block are never included by one pass and left out by another.
/// <para>
/// Most conditions are evaluated once for the build, before any declaration is looked up. The
/// ones inside a macro body, a <c>.repeat</c> or an <c>.each</c> cannot be, because they may name
/// what the <see cref="Expansion"/> binds. The overload that takes a model evaluates those once
/// per expansion.
/// </para>
/// </summary>
public static class ConditionChain
{
    /// <summary>
    /// Returns each of <paramref name="children"/> from index <paramref name="from"/> on, with
    /// whether it is included. A line, and a block that is not part of a chain, is always
    /// included, and it ends any chain that came before it.
    /// </summary>
    /// <param name="children">The siblings to walk.</param>
    /// <param name="from">The index of the first sibling to walk.</param>
    /// <param name="holds">
    /// The function that decides whether a branch is included. It is called once for each branch
    /// of a chain, in order, and is told whether an earlier branch of the chain was included.
    /// </param>
    /// <param name="orphaned">
    /// The action called for an <c>.elseif</c> or <c>.else</c> that continues no chain, which is
    /// never included. It may be null when the orphan has been reported elsewhere.
    /// </param>
    public static IEnumerable<(SyntaxNode Node, bool Included)> Walk(
        IReadOnlyList<SyntaxNode> children, int from, Func<BlockSyntax, bool, bool> holds, Action<BlockSyntax>? orphaned = null)
    {
        var chaining = false;
        var taken = false;
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            switch (child is BlockSyntax block ? block.Opener.Statement : null)
            {
                case IfDirectiveSyntax:
                    chaining = true;
                    taken = holds((BlockSyntax)child, false);
                    yield return (child, taken);
                    break;

                case ElseIfDirectiveSyntax or ElseDirectiveSyntax when !chaining:
                    orphaned?.Invoke((BlockSyntax)child);
                    yield return (child, false);
                    break;

                case ElseIfDirectiveSyntax or ElseDirectiveSyntax:
                    var take = holds((BlockSyntax)child, taken);
                    taken |= take;
                    yield return (child, take);
                    break;

                default:
                    chaining = false;
                    yield return (child, true);
                    break;
            }
        }
    }

    /// <summary>
    /// Returns each of <paramref name="children"/> from index <paramref name="from"/> on, with
    /// whether it is included in the expansion <paramref name="on"/>. The build's decision is
    /// used for a condition the build evaluated, and any other condition is evaluated with the
    /// model.
    /// </summary>
    /// <param name="model">The model of the file the siblings are in.</param>
    /// <param name="children">The siblings to walk.</param>
    /// <param name="from">The index of the first sibling to walk.</param>
    /// <param name="on">The expansion the siblings are in.</param>
    /// <param name="diagnostics">
    /// The list that receives the problems in each condition evaluated here, or null to report
    /// nothing. No pass over the symbols reaches a condition in an expansion, so the one pass
    /// that walks every expansion once passes its list, and every other caller passes null.
    /// </param>
    public static IEnumerable<(SyntaxNode Node, bool Included)> Walk(
        SemanticModel model, IReadOnlyList<SyntaxNode> children, int from, Expansion? on, List<Diagnostic>? diagnostics = null) =>
        Walk(children, from, (block, already) => Holds(model, block, already, on, diagnostics));

    private static bool Holds(SemanticModel model, BlockSyntax block, bool already, Expansion? on, List<Diagnostic>? diagnostics)
    {
        if (model.Configuration.Answered(block))
            return model.Configuration.Includes(block);
        if (already)
            return false;
        if (block.Opener.Statement is not ConditionalDirectiveSyntax conditional)
            return block.Opener.Statement is ElseDirectiveSyntax;
        if (diagnostics is not null)
            model.Check(conditional.Condition, diagnostics, on);
        return model.ValueOf(conditional.Condition, on).AsNumber() is { } value && value != 0;
    }
}
