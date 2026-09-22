using Norristown.Layout;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Whether each routine a <c>.fallthrough</c> says runs into another does, where the file it is
/// in cannot say on its own: into another module's routine, or past a <c>.place</c>. Within one
/// translation unit nt65 lays out every byte, each segment's in the order the unit writes it,
/// and the answer is read off that layout: what a placed module writes to other segments does
/// not stand between two routines of one. Across units the order of the bytes is the link's,
/// which nt65 does not know, so the claim is an error there.
/// </summary>
internal static class RunningOnChecks
{
    /// <summary>
    /// What is wrong with the claims the files of <paramref name="program"/> made, with each
    /// file's layout and flow at the same index in <paramref name="layouts"/> and
    /// <paramref name="flows"/>.
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
                var span = claim.Written.Tree.GetSpan(claim.Written.Span);
                var unit = placements.UnitOf(model.Tree);
                if (unit is null || placements.UnitOf(routine.Tree)?.Root.Path != unit.Root.Path)
                {
                    found.Add(new Diagnostic(span, routine.Tree.Path == model.Tree.Path
                        ? Catalogue.FallthroughNotAdjacent.Says(routine.DisplayName)
                        : Catalogue.FallthroughNotPlaced.Says(routine.DisplayName, routine.Module ?? routine.Tree.Path)));
                    continue;
                }
                if (!laid.TryGetValue(unit.Root.Path, out var layout))
                {
                    laid[unit.Root.Path] = layout = UnitLayout.Of(
                        unit, tree => layoutOf.GetValueOrDefault(tree.Path), placements);
                }
                var here = layouts[i].Placed(claim.Statement, claim.On) is { } statement
                    ? layout.Where(model.Tree, statement) is { } start ? (start.Run, start.Offset + statement.Length) : ((int, int)?)null
                    : null;
                var there = layoutOf.GetValueOrDefault(routine.Tree.Path)?.Placed(routine) is { } label
                    ? layout.Where(routine.Tree, label)
                    : null;
                if (here is null || here != there)
                {
                    found.Add(new Diagnostic(span, Catalogue.FallthroughNotAdjacent.Says(routine.DisplayName)));
                }
            }
            diagnostics.AddRange(Family.Collapsed(model.Families, found));
        }
        return diagnostics;
    }
}
