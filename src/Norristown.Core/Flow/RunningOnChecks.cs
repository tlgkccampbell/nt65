using Norristown.Layout;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Checks each <c>.fallthrough</c> claim that the file containing it cannot check on its own,
/// which is one that runs into another module's routine or across a <c>.place</c>. Within one
/// translation unit nt65 lays out every byte, each segment's bytes in the order the unit emits
/// them, and the answer is read off that layout. Bytes that a module emitted by <c>.place</c>
/// writes to other segments do not come between two routines of one segment. Across units the
/// linker decides the order of the bytes, which nt65 does not know, so such a claim is an error.
/// </summary>
internal static class RunningOnChecks
{
    /// <summary>
    /// Reports a diagnostic for each <c>.fallthrough</c> claim in the files of
    /// <paramref name="program"/> whose routines are not adjacent, or that crosses from one
    /// translation unit into another. Each file's layout and flow are at the same index in
    /// <paramref name="layouts"/> and <paramref name="flows"/>.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Check(
        ProgramModel program, IReadOnlyList<CodeLayout> layouts, IReadOnlyList<ControlFlow> flows, Placements placements)
    {
        var diagnostics = new List<Diagnostic>();
        var layoutOf = new Dictionary<string, CodeLayout>(StringComparer.Ordinal);
        for (var i = 0; i < program.Files.Count && i < layouts.Count; i++)
            layoutOf.TryAdd(program.Files[i].Tree.Path, layouts[i]);
        var laid = new Dictionary<string, UnitLayout>(StringComparer.Ordinal);

        for (var i = 0; i < program.Files.Count && i < flows.Count; i++)
        {
            if (flows[i].RunningOn.Count == 0)
                continue;
            var model = program.Files[i];
            var found = new List<Diagnostic>();
            foreach (var claim in flows[i].RunningOn)
            {
                var routine = program.Current(claim.Routine);
                var span = claim.Target.Tree.GetSpan(claim.Target.Span);
                var unit = placements.UnitOf(model.Tree);
                if (unit is null || placements.UnitOf(routine.Tree)?.Root.Path != unit.Root.Path)
                {
                    found.Add(new Diagnostic(span, routine.Tree.Path == model.Tree.Path
                        ? Catalogue.FallthroughNotAdjacent.Message(routine.DisplayName)
                        : Catalogue.FallthroughNotPlaced.Message(routine.DisplayName, routine.Module ?? routine.Tree.Path)));
                    continue;
                }
                if (!laid.TryGetValue(unit.Root.Path, out var layout))
                {
                    laid[unit.Root.Path] = layout = UnitLayout.Of(
                        unit, tree => layoutOf.GetValueOrDefault(tree.Path), placements);
                }
                var here = layouts[i].PositionOf(claim.Statement, claim.On) is { } statement
                    ? layout.Where(model.Tree, statement) is { } start ? (start.Run, start.Offset + statement.Length) : ((int, int)?)null
                    : null;
                var there = layoutOf.GetValueOrDefault(routine.Tree.Path)?.PositionOf(routine) is { } label
                    ? layout.Where(routine.Tree, label)
                    : null;
                if (here is null || here != there)
                {
                    found.Add(new Diagnostic(span, Catalogue.FallthroughNotAdjacent.Message(routine.DisplayName)));
                }
            }
            diagnostics.AddRange(Family.Collapsed(model.Families, found));
        }
        return diagnostics;
    }
}
