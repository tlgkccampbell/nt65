using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Where the bytes of a translation unit land, across the modules in it: each file's runs of
/// known distances, joined where placement puts one module's bytes directly after another's.
/// <para>
/// Each file is laid out on its own, and knows no distance across a <c>.place</c>. This walks
/// the files in the order the unit writes them, the placed module's steps where its
/// <c>.place</c> stands, and follows each segment's bytes as ca65 will write them: two lines
/// of one segment with nothing of that segment between them are at a known distance, whichever
/// files they are in. An <c>.align</c> ends what is known in its segment, as it does in a file.
/// </para>
/// </summary>
public sealed class UnitLayout
{
    // For each file's run of known distances, where its pieces stand in the unit's runs: the
    // offset in the file's run a piece starts at, and the unit's run and offset it is at there.
    private readonly Dictionary<(string File, int Run), List<(int From, int Run, int At)>> pieces = [];

    // Where each segment's bytes have reached, as the unit's run and the offset in it.
    private readonly Dictionary<string, (int Run, int At)> reached = new(StringComparer.Ordinal);
    private readonly Func<SyntaxTree, CodeLayout?> layoutOf;
    private readonly Placements placements;
    private int nextRun;

    private UnitLayout(Func<SyntaxTree, CodeLayout?> layoutOf, Placements placements)
    {
        this.layoutOf = layoutOf;
        this.placements = placements;
    }

    /// <summary>
    /// Lays out <paramref name="unit"/> from the layouts <paramref name="layoutOf"/> gives for
    /// each of its files, placing what <paramref name="placements"/> says each <c>.place</c> places.
    /// </summary>
    public static UnitLayout Of(TranslationUnit unit, Func<SyntaxTree, CodeLayout?> layoutOf, Placements placements)
    {
        var laid = new UnitLayout(layoutOf, placements);
        laid.Walk(unit.Root);
        return laid;
    }

    /// <summary>
    /// Where <paramref name="placement"/>, which is where something stands in the layout of
    /// <paramref name="tree"/>, stands in the unit: its run and offset, or null where nothing
    /// the unit laid out is at a known distance from it.
    /// </summary>
    public (int Run, int Offset)? Where(SyntaxTree tree, Placement placement)
    {
        if (!pieces.TryGetValue((tree.Path, placement.Stream), out var list))
            return null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].From <= placement.Offset)
                return (list[i].Run, list[i].At + placement.Offset - list[i].From);
        }
        return null;
    }

    /// <summary>Follows one file's bytes, and each module it places where the <c>.place</c> stands.</summary>
    private void Walk(SyntaxTree tree)
    {
        if (layoutOf(tree) is not { } layout)
            return;
        var points = layout.PlacePoints;
        var next = 0;
        var seen = new HashSet<(int, Expansion?)>();
        for (var i = 0; i < layout.Steps.Count; i++)
        {
            while (next < points.Count && points[next].Step == i)
                Splice(tree, points[next++]);
            var step = layout.Steps[i];
            if (step.Segment is not { } segment || layout.Placed(step.Statement, step.On) is not { } placed
                || !seen.Add((step.Statement.Position, step.On)))
            {
                continue;
            }
            var (run, at) = Reached(segment);
            if (Where(tree, placed) != (run, at))
                Add(tree, placed.Stream, placed.Offset, run, at);
            reached[segment] = placed.Length == DataLengths.Unpredictable ? (nextRun++, 0) : (run, at + placed.Length);
        }
        while (next < points.Count)
            Splice(tree, points[next++]);
    }

    /// <summary>
    /// The module a <c>.place</c> places, laid out where it stands. The file's bytes after the
    /// line start a run of its own, which goes on from wherever the placed module left the
    /// segment the file is writing to.
    /// </summary>
    private void Splice(SyntaxTree tree, PlacePoint point)
    {
        if (placements.Placed(point.Directive) is { } placed)
            Walk(placed);
        if (point.Segment is { } segment)
        {
            var (run, at) = Reached(segment);
            Add(tree, point.Resumes, 0, run, at);
        }
    }

    /// <summary>Where a segment's bytes have reached, starting a run for one nothing has written to yet.</summary>
    private (int Run, int At) Reached(string segment)
    {
        if (!reached.TryGetValue(segment, out var now))
            reached[segment] = now = (nextRun++, 0);
        return now;
    }

    private void Add(SyntaxTree tree, int fileRun, int from, int run, int at)
    {
        if (!pieces.TryGetValue((tree.Path, fileRun), out var list))
            pieces[(tree.Path, fileRun)] = list = [];
        list.Add((from, run, at));
    }
}
