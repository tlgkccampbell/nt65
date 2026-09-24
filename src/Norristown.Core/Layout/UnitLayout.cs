using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Represents where the bytes of a translation unit land across the modules in it. Each file's
/// runs of known distances are joined where placement puts one module's bytes directly after
/// another's.
/// <para>
/// Each file is laid out on its own and knows no distance across a <c>.place</c>. This class
/// walks the files in the order the unit emits them, with the placed module's steps where its
/// <c>.place</c> stands, and follows each segment's bytes as ca65 will emit them. Two lines of
/// one segment with nothing of that segment between them are at a known distance, even when they
/// are in different files. An <c>.align</c> ends what is known in its segment, as it does in a
/// file.
/// </para>
/// </summary>
public sealed class UnitLayout
{
    // For each of a file's runs, the parts it is split into among the unit's runs. Each entry
    // holds the offset in the file's run at which a part starts, and the unit's run and offset at
    // which that part starts.
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
    /// each of its files, placing at each <c>.place</c> the module that <paramref name="placements"/>
    /// assigns to it.
    /// </summary>
    public static UnitLayout Of(TranslationUnit unit, Func<SyntaxTree, CodeLayout?> layoutOf, Placements placements)
    {
        var laid = new UnitLayout(layoutOf, placements);
        laid.Walk(unit.Root);
        return laid;
    }

    /// <summary>
    /// Translates <paramref name="position"/>, a position in the layout of
    /// <paramref name="tree"/>, to a position in the unit. Returns the unit's run and the offset
    /// in it, or null where nothing the unit laid out is at a known distance from it.
    /// </summary>
    public (int Run, int Offset)? Where(SyntaxTree tree, BytePosition position)
    {
        if (!pieces.TryGetValue((tree.Path, position.Stream), out var list))
            return null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].From <= position.Offset)
                return (list[i].Run, list[i].At + position.Offset - list[i].From);
        }
        return null;
    }

    /// <summary>
    /// Follows one file's bytes, and the bytes of each module it places where the <c>.place</c>
    /// stands.
    /// </summary>
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
            if (step.Segment is not { } segment || layout.PositionOf(step.Statement, step.On) is not { } position
                || !seen.Add((step.Statement.Position, step.On)))
            {
                continue;
            }
            var (run, at) = Reached(segment);
            if (Where(tree, position) != (run, at))
                Add(tree, position.Stream, position.Offset, run, at);
            reached[segment] = position.Length == DataLengths.Unpredictable ? (nextRun++, 0) : (run, at + position.Length);
        }
        while (next < points.Count)
            Splice(tree, points[next++]);
    }

    /// <summary>
    /// Lays out the module a <c>.place</c> places where the directive stands. The file's bytes
    /// after the directive start a run of their own, which continues from where the placed module
    /// left the segment the file is emitting to.
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

    /// <summary>
    /// Returns where a segment's bytes have reached, starting a new run for a segment that nothing
    /// has emitted to yet.
    /// </summary>
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
