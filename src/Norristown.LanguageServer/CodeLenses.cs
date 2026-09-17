using Norristown.Flow;
using Norristown.Layout;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What is shown above a routine, and above each inline <c>.scope</c> block of one: what a
/// pass through it costs. A <c>.scope</c> at file level holds declarations and no code, so it
/// has nothing to count; one inside a routine is a part of that routine, and does.
/// </summary>
internal static class CodeLenses
{
    /// <summary>The lenses for <paramref name="tree"/>, in position order.</summary>
    public static IReadOnlyList<Protocol.CodeLens> In(SyntaxTree tree, ControlFlow? flow)
    {
        var lenses = new List<(int At, Protocol.CodeLens Lens)>();
        foreach (var region in flow?.Regions ?? [])
        {
            if (region.Routine.Tree != tree)
                continue;
            if (Spell(region.Cost, region.Total, "never returns") is { } text)
                Add(region.Routine.NameSpan, text);
            foreach (var scope in region.Scopes)
            {
                if (Spell(scope.Cost, null, null) is { } inline)
                    Add(scope.Opener, inline);
            }
        }

        void Add(TextSpan at, string text) =>
            lenses.Add((at.Start, new Protocol.CodeLens(Lsp.ToRange(tree, at), new Protocol.Command(text, ""))));
        return [.. lenses.OrderBy(lens => lens.At).Select(lens => lens.Lens)];
    }

    /// <summary>
    /// What it costs, as the lens says it: an interval where a path has a longest, the fewest
    /// and a <c>+</c> where it loops, and what it costs with its calls after that, or a word
    /// saying they are not in the count when nt65 cannot follow one of them.
    /// <paramref name="endless"/> is what to say where no path leaves at all.
    /// </summary>
    private static string? Spell(RoutineCost cost, RoutineCost? total, string? endless)
    {
        // A routine no path leaves has no pass to cost, which is worth saying rather than
        // leaving a line that looks as though the lens failed on it.
        if (!cost.Ends)
            return endless;
        if (cost is not { Least: { } least })
            return null;
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

    /// <summary>A count as it is shown: an interval, or the fewest and a <c>+</c> where there is no most.</summary>
    private static string Count(int least, int? most) =>
        most is { } bound ? Lsp.Spell(new CycleCount(least, bound)) : $"{least}+ cycles";
}
