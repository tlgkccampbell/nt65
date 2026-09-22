using Norristown.Flow;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What is shown above a routine, and above each inline <c>.scope</c> block of one: what a
/// pass through it costs, and which registers it hands on. A <c>.scope</c> at file level holds
/// declarations and no code, so it has neither; one inside a routine is a part of that routine,
/// and has both.
/// <para>
/// The two are lenses of their own rather than one line, because they answer different
/// questions and a reader looking for one should not have to read past the other.
/// </para>
/// </summary>
internal static class CodeLenses
{
    /// <summary>The lenses for <paramref name="tree"/>, in position order.</summary>
    public static IReadOnlyList<Protocol.CodeLens> In(SyntaxTree tree, ControlFlow? flow)
    {
        var found = new List<(TextSpan At, int Kind, string Routine, string Text)>();
        foreach (var region in flow?.Regions ?? [])
        {
            if (region.Routine.Tree != tree)
                continue;
            if (Spell(region.Cost, region.Total, "never returns") is { } cost)
                found.Add((region.Routine.NameSpan, 0, region.Routine.Name, cost));
            if (Kept(region) is { } kept)
                found.Add((region.Routine.NameSpan, 1, region.Routine.Name, kept));
            foreach (var scope in region.Scopes)
            {
                if (Spell(scope.Cost, null, null) is { } inline)
                    found.Add((scope.Opener, 0, region.Routine.Name, inline));
            }
            foreach (var scope in region.ScopeRegisters)
            {
                if (Spell(scope.Kept, scope.Complete) is { } inline)
                    found.Add((scope.Opener, 1, region.Routine.Name, inline));
            }
        }

        // Every instance of a family is declared on one line and answers at it. Where every
        // instance answers the same the line says it once; where they differ, each says which
        // instance it is about. The two kinds are grouped apart, so one of them differing
        // between instances does not make the other say which instance it is about too.
        var lenses = new List<(int At, int Kind, Protocol.CodeLens Lens)>();
        foreach (var at in found.GroupBy(lens => (lens.At, lens.Kind)))
        {
            var texts = at.Select(lens => lens.Text).Distinct(StringComparer.Ordinal).ToList();
            if (texts.Count == 1)
                Add(at.Key.At, at.Key.Kind, texts[0]);
            else
            {
                foreach (var lens in at)
                    Add(at.Key.At, at.Key.Kind, $"{lens.Routine}: {lens.Text}");
            }
        }

        void Add(TextSpan at, int kind, string text) =>
            lenses.Add((at.Start, kind, new Protocol.CodeLens(Lsp.ToRange(tree, at), new Protocol.Command(text, ""))));
        return [.. lenses.OrderBy(lens => lens.At).ThenBy(lens => lens.Kind).Select(lens => lens.Lens)];
    }

    /// <summary>
    /// What it costs, as the lens says it: an interval where a path has a longest, the fewest
    /// and a <c>+</c> where it loops, and what it costs with its calls after that, or a word
    /// saying they are not in the count when nt65 cannot follow one of them.
    /// <paramref name="endless"/> is what to say where no path leaves at all.
    /// <para>
    /// The hover says it too, at the declaration and at every call, and calls this so that the
    /// two agree word for word.
    /// </para>
    /// </summary>
    internal static string? Spell(RoutineCost cost, RoutineCost? total, string? endless)
    {
        // A routine no path leaves has no pass to cost, which is worth saying rather than
        // leaving a line that looks as though the lens failed on it.
        if (!cost.Ends)
            return endless;
        // A routine holding an instruction whose time only the run says has no count, and the
        // word for it is better than no lens at all.
        if (cost is not { Least: { } least })
            return cost.Uncounted is { } why ? $"not counted: {why}" : null;
        var count = Count(least, cost.Most);
        if (cost.Loops)
            count += ", loops";
        if (!cost.Calls)
            return count;
        if (total is not { Least: { } with })
            return $"{count}, not counting calls";

        // Where a routine hands control to one that never comes back, the count is what it
        // takes to get there, which is worth saying plainly rather than through the calls.
        var ending = total.Value.Ends ? "" : ", then never returns";

        // What it calls may cost nothing at all to this count, and saying the same number
        // twice says less than saying it once.
        return with == cost.Least && total.Value.Most == cost.Most
            ? count + ending
            : $"{count}, {Count(with, total.Value.Most)} with calls{ending}";
    }

    /// <summary>
    /// Which registers a routine hands back as it was entered with them. A routine nt65 could
    /// not cost is one it could not lay out either, and what it keeps would be worked out from
    /// bytes that are not the ones it would assemble to.
    /// </summary>
    private static string? Kept(FlowRegion region) => region.Total.Ends
        ? Spell(region.Registers.Kept, region.Registers.Complete)
        : null;

    /// <summary>
    /// What is kept, as a lens says it. The hover spells the list the same way and leaves the
    /// word off, because the key beside it already says what the list is.
    /// </summary>
    private static string Spell(Registers kept, bool complete) => $"preserves {Lsp.Spell(kept, complete)}";

    /// <summary>A count as it is shown: an interval, or the fewest and a <c>+</c> where there is no most.</summary>
    private static string Count(int least, int? most) =>
        most is { } bound ? Lsp.Spell(new CycleCount(least, bound)) : $"{least}+ cycles";
}
