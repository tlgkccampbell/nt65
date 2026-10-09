using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents the stores into code that the line at the caret takes part in, for an editor to
/// show. On a store with a <c>.patch</c>, or on the <c>.patch</c> itself, each link leads to the
/// instruction the store writes into. On an instruction that a <c>.patch</c> names, each link leads
/// from a store that writes into it.
/// <para>
/// Every link is declared, because a store into code without a <c>.patch</c> is already an
/// error. Nothing here is inferred.
/// </para>
/// </summary>
/// <param name="Routine">The span of the name of the routine that holds the first link's store.</param>
/// <param name="Links">Each link, in the order the stores come in the file.</param>
public sealed record PatchLinks(TextSpan Routine, IReadOnlyList<PatchLink> Links)
{
    /// <summary>
    /// Returns the stores into code that the line at <paramref name="position"/> in
    /// <paramref name="model"/>'s file takes part in, or null where it takes part in none. Only
    /// links with both ends in the file are returned. A store or an instruction in a macro's
    /// body is shown on the call that expanded it.
    /// </summary>
    public static PatchLinks? At(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var tree = model.Tree;
        if (position > tree.Text.Length || analysis.FileFor(tree.Path) is not { } file)
            return null;
        var caret = tree.GetLineIndex(position);
        bool On(TextSpan span) => tree.GetLineIndex(span.Start) == caret;

        // Each label a `.patch` names, with the stores that name it. This walks the steps as
        // ControlFlow does when it finds the patched instructions, so the two agree.
        var naming = new Dictionary<Symbol, List<(Step Store, PatchDirectiveSyntax Patch)>>();
        foreach (var step in file.Layout.Steps)
        {
            foreach (var patch in file.Flow.AnnotationsOf(step).OfType<PatchDirectiveSyntax>())
            {
                if (Targets.Of(file.Model, patch.Target, step.On)?.Symbol is { } symbol)
                {
                    if (!naming.TryGetValue(symbol, out var stores))
                        naming[symbol] = stores = [];
                    stores.Add((step, patch));
                }
            }
        }
        if (naming.Count == 0)
            return null;

        var links = new List<PatchLink>();
        Symbol? routine = null;
        List<(Step Store, PatchDirectiveSyntax Patch)>? pending = null;
        foreach (var step in file.Layout.Steps)
        {
            if (step.Label is { } label)
            {
                if (naming.TryGetValue(label, out var stores))
                    (pending ??= []).AddRange(stores);
                continue;
            }
            if (pending is not null && step.Statement is InstructionStatementSyntax
                && StepLines.Of(tree, step) is { Span: var target })
            {
                foreach (var (store, patch) in pending)
                {
                    if (StepLines.Of(tree, store) is not { Span: var from })
                        continue;
                    var onPatch = patch.Tree == tree && On(patch.Span);
                    if (!(On(from) || On(target) || onPatch)
                        || links.Any(link => link.Store == from && link.Target == target))
                    {
                        continue;
                    }
                    links.Add(new PatchLink(
                        from,
                        target,
                        patch.Target!.GetText().Trim(),
                        [.. patch.Variants.Select(variant => variant.GetText().Trim())]));
                    routine ??= store.Routine;
                }
            }
            pending = null;
        }
        if (links.Count == 0)
            return null;
        links.Sort((left, right) => left.Store.Start.CompareTo(right.Store.Start));
        var opener = routine is { } named && named.Tree == tree ? named.NameSpan : links[0].Store;
        return new PatchLinks(opener, links);
    }
}
