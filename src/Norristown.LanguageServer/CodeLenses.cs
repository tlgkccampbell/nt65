using Norristown.Flow;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The code lenses shown above a routine and above each inline <c>.scope</c> block inside one:
/// how many cycles a pass through it costs, and which registers it preserves. A <c>.scope</c>
/// at file level holds only declarations and no code, so it gets neither lens; a <c>.scope</c>
/// inside a routine is part of that routine's code and gets both.
/// <para>
/// Cost and preserved registers are separate lenses rather than one line, because they answer
/// different questions and a reader looking for one should not have to read past the other.
/// </para>
/// <para>
/// The routines a family declares get no lenses. Every instance is declared on the family's
/// one line, so their lenses would all land on it, one per instance, and make a line too long
/// to read. The hover on that line gives each instance's cost and registers instead.
/// </para>
/// </summary>
internal static class CodeLenses
{
    /// <summary>The lenses for <paramref name="tree"/>, in position order.</summary>
    public static IReadOnlyList<Protocol.CodeLens> In(
        SyntaxTree tree, IReadOnlyList<Family> families, ControlFlow? flow)
    {
        var instances = families
            .SelectMany(family => family.Instances)
            .Where(instance => instance.Tree == tree)
            .Select(instance => instance.NameSpan)
            .ToHashSet();
        var lenses = new List<(int At, int Kind, Protocol.CodeLens Lens)>();
        foreach (var region in flow?.Regions ?? [])
        {
            if (region.Routine.Tree != tree || instances.Contains(region.Routine.NameSpan))
                continue;
            if (Spell(region.Cost, region.Total, "never returns", true) is { } cost)
                Add(region.Routine.NameSpan, 0, cost);
            if (Kept(region) is { } kept)
                Add(region.Routine.NameSpan, 1, kept);
            foreach (var scope in region.Scopes)
            {
                if (Spell(scope.Cost, null, null, true) is { } inline)
                    Add(scope.Opener, 0, inline);
            }
            foreach (var scope in region.ScopeRegisters)
                Add(scope.Opener, 1, Spell(scope.Kept, scope.Complete));
        }

        void Add(TextSpan at, int kind, string text) =>
            lenses.Add((at.Start, kind, new Protocol.CodeLens(Lsp.ToRange(tree, at), new Protocol.Command(text, ""))));
        return [.. lenses.OrderBy(lens => lens.At).ThenBy(lens => lens.Kind).Select(lens => lens.Lens)];
    }

    /// <summary>
    /// A routine's cost as the lens shows it: a cycle interval when the longest path is bounded,
    /// or the minimum followed by <c>+</c> when it is not, with <c>, loops</c> when the routine
    /// loops; then the cost including its calls, and what that leaves out because nt65 cannot
    /// count it. <paramref name="endless"/> is the text to show when no path leaves the routine
    /// at all.
    /// <para>
    /// The hover shows the cost too, at the declaration and at every call, and calls this so
    /// that the two agree word for word. It lists what is left out on rows of its own, with
    /// why, so it asks for the cost without <paramref name="excluding"/>.
    /// </para>
    /// </summary>
    internal static string? Spell(RoutineCost cost, RoutineCost? total, string? endless, bool excluding)
    {
        // A routine that no path leaves has no complete pass to cost. Say so, rather than show
        // nothing and look as though the lens failed.
        if (!cost.Ends)
            return endless;
        // A routine containing an instruction whose cycle count is only known at run time has
        // no count; saying why is better than showing no lens at all.
        if (cost is not { Least: { } least })
            return cost.Uncounted is { } why ? $"not counted: {why}" : null;
        var count = Count(least, cost.Most);
        if (cost.Loops)
            count += ", loops";
        if (!cost.Calls)
            return count;

        // An inline scope has no cost with its calls, and a routine has none where its own
        // instructions have no count.
        if (total is not { Least: { } with })
            return $"{count}, excluding calls";

        // Where a routine passes control to one that never returns, the count covers only the
        // path up to that point, so the lens says so explicitly.
        var ending = total.Value.Ends ? "" : ", then never returns";
        var excluded = total.Value.Excluded ?? [];

        // When the calls add nothing to the count, show the number once rather than repeating
        // it as the with-calls figure. The figure with calls is only a number, since the
        // count beside it already says what it counts.
        if (with == cost.Least && total.Value.Most == cost.Most && excluded.Count == 0)
            return count + ending;
        var left = excluding && excluded.Count > 0 ? $", excluding {Named(excluded)}" : "";
        return $"{count}, {Number(with, total.Value.Most)} with calls{left}{ending}";
    }

    /// <summary>
    /// What a cost with calls leaves out, as the lens names it: two at most, then how many
    /// more, since a lens shares its line and the hover lists them all.
    /// </summary>
    private static string Named(IReadOnlyList<Exclusion> excluded) => excluded.Count switch
    {
        1 => excluded[0].What,
        2 => $"{excluded[0].What} and {excluded[1].What}",
        _ => $"{excluded[0].What}, {excluded[1].What} and {excluded.Count - 2} more",
    };

    /// <summary>
    /// Which registers a routine returns holding the values it was entered with, or null when
    /// no path through the routine, its calls included, returns.
    /// </summary>
    private static string? Kept(FlowRegion region) => region.Total.Ends
        ? Spell(region.Registers.Kept, region.Registers.Complete)
        : null;

    /// <summary>
    /// The preserved registers as the lens shows them. The hover formats the list the same way
    /// but leaves off the word "preserves", because the label beside it already says what the
    /// list is.
    /// </summary>
    private static string Spell(Registers kept, bool complete) => $"preserves {Lsp.Spell(kept, complete)}";

    /// <summary>A cycle count as shown: an interval, or the minimum and a <c>+</c> when there is no maximum.</summary>
    private static string Count(int least, int? most) =>
        most is { } bound ? Lsp.Spell(new CycleCount(least, bound)) : $"{least}+ cycles";

    /// <summary>The same count without the word <c>cycles</c>.</summary>
    private static string Number(int least, int? most) =>
        most is { } bound ? new CycleCount(least, bound).ToString() : $"{least}+";
}
