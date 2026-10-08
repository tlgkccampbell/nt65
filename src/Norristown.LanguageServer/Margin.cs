using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the <c>nt65/margin</c> request. It converts the loops that <see cref="LoopBrackets"/>
/// finds in every routine of a document, and the arrows that <see cref="FlowArrows"/> finds for
/// the caret, into lines, and gives each bracket and arrow the column its upright is drawn in.
/// <para>
/// Brackets take the columns furthest from the code, with the outer loop furthest, and arrows take
/// the columns nearest it. A bracket whose loop starts at its header stands for the branches back
/// to that header, so those branches are not sent as arrows as well.
/// </para>
/// </summary>
internal static class Margin
{
    /// <summary>
    /// The most columns the brackets and arrows of a routine take. Every column is paid for on
    /// every line of the routine, because the client draws them as a prefix that shifts the code
    /// right.
    /// </summary>
    public const int MostColumns = 4;

    /// <summary>
    /// Returns, where <paramref name="brackets"/> is set, the brackets of every routine in
    /// <paramref name="model"/>'s file that has loops and, where <paramref name="arrows"/> is set,
    /// the arrows of the routine that holds <paramref name="position"/>. It returns null where
    /// there is nothing to draw.
    /// </summary>
    public static Protocol.MarginResult? At(ProgramAnalysis analysis, SemanticModel model, int position, bool brackets, bool arrows)
    {
        var routines = (brackets ? LoopBrackets.In(analysis, model) : [])
            .Select(found => (found.Routine, found.Loops, Arrows: (IReadOnlyList<FlowArrow>)[]))
            .ToList();
        if (arrows && FlowArrows.At(analysis, model, position) is { Arrows.Count: > 0 } caret)
        {
            var at = routines.FindIndex(routine => routine.Routine == caret.Routine);
            if (at >= 0)
                routines[at] = routines[at] with { Arrows = caret.Arrows };
            else
                routines.Add((caret.Routine, [], caret.Arrows));
        }
        if (routines.Count == 0)
            return null;
        return new Protocol.MarginResult(
        [
            .. routines
                .OrderBy(routine => routine.Routine.Start)
                .Select(routine => Lay(model.Tree, routine.Routine, routine.Loops, routine.Arrows)),
        ]);
    }

    /// <summary>Returns how one routine's brackets and arrows are drawn.</summary>
    private static Protocol.MarginRoutine Lay(
        SyntaxTree tree, TextSpan routine, IReadOnlyList<LoopBracket> loops, IReadOnlyList<FlowArrow> arrows)
    {
        var brackets = loops
            .Select(loop => (
                Top: Line(loop.Top),
                Bottom: Line(loop.Bottom),
                Header: Line(loop.Header),
                Latches: loop.Latches.Select(Line).ToList(),
                loop.Trips))
            .ToList();
        var outer = Outer([.. brackets.Select(bracket => (bracket.Top, bracket.Bottom))]);
        var bracketColumns = outer.Max(column => column + 1) ?? 0;

        // A drawn bracket that starts at its header is the drawing of the branches back to it.
        var merged = new HashSet<(int From, int To)>();
        for (var i = 0; i < brackets.Count; i++)
        {
            if (outer[i] is not null && brackets[i].Header == brackets[i].Top)
                merged.UnionWith(brackets[i].Latches.Select(latch => (latch, brackets[i].Header)));
        }
        var shown = arrows
            .Select(arrow => (Arrow: arrow, From: Line(arrow.From), To: Line(arrow.To)))
            .Where(arrow => !merged.Contains((arrow.From, arrow.To)))
            .ToList();
        var (columns, arrowColumns) = Columns([.. shown.Select(arrow => (arrow.From, arrow.To))], MostColumns - bracketColumns);
        var count = bracketColumns + arrowColumns;

        return new Protocol.MarginRoutine(
            Line(routine),
            tree.GetLineIndex(Math.Max(routine.Start, routine.End - 1)),
            count,
            [.. brackets.Select((bracket, index) =>
            {
                var head = outer[index] is not null && bracket.Header == bracket.Top;
                return new Protocol.MarginBracket(
                    bracket.Top,
                    bracket.Bottom,
                    head,
                    head ? [.. bracket.Latches.Where(latch => latch > bracket.Top && latch < bracket.Bottom)] : [],
                    count - 1 - outer[index],
                    bracket.Trips);
            })],
            [.. shown.Select((arrow, index) => new Protocol.MarginArrow(
                arrow.From, arrow.To, columns[index], arrow.Arrow.IsDeclared, arrow.Arrow.IsProved, arrow.Arrow.IsReached))]);

        int Line(TextSpan span) => tree.GetLineIndex(span.Start);
    }

    /// <summary>
    /// Returns the column each bracket's upright goes in, counted from the one furthest from the
    /// code. Longer brackets go further out, so that a loop inside another is drawn inside it. Two
    /// brackets share a column only where their lines do not overlap. A loop on one line has no
    /// column, and nor has one that finds none free.
    /// </summary>
    /// <param name="brackets">The first and last line of each loop.</param>
    private static int?[] Outer(IReadOnlyList<(int Top, int Bottom)> brackets)
    {
        var columns = new int?[brackets.Count];
        var used = new List<List<(int Top, int Bottom)>>();
        var order = Enumerable.Range(0, brackets.Count)
            .Where(index => brackets[index].Bottom > brackets[index].Top)
            .OrderByDescending(index => brackets[index].Bottom - brackets[index].Top)
            .ThenBy(index => brackets[index].Top);
        foreach (var index in order)
        {
            var (top, bottom) = brackets[index];
            var column = 0;
            while (column < used.Count && used[column].Any(span => span.Top <= bottom && top <= span.Bottom))
                column++;
            if (column >= MostColumns)
                continue;
            if (column == used.Count)
                used.Add([]);
            used[column].Add((top, bottom));
            columns[index] = column;
        }
        return columns;
    }

    /// <summary>
    /// Returns the column each arrow's upright goes in, and how many columns there are. Shorter
    /// arrows go nearer the code, so that an arrow inside another is drawn inside it. Two arrows
    /// share a column only where their lines do not overlap, counting the lines at their ends.
    /// </summary>
    /// <param name="arrows">The line each arrow starts on and the line it ends on.</param>
    /// <param name="most">The most columns the arrows may take.</param>
    private static (int?[] Columns, int Count) Columns(IReadOnlyList<(int From, int To)> arrows, int most)
    {
        var columns = new int?[arrows.Count];
        var used = new List<List<(int Low, int High)>>();
        var order = Enumerable.Range(0, arrows.Count)
            .Select(index => (Index: index, Low: Math.Min(arrows[index].From, arrows[index].To), High: Math.Max(arrows[index].From, arrows[index].To)))
            .OrderBy(arrow => arrow.High - arrow.Low)
            .ThenBy(arrow => arrow.Low);
        foreach (var (index, low, high) in order)
        {
            var column = 0;
            while (column < used.Count && used[column].Any(span => span.Low <= high && low <= span.High))
                column++;
            if (column >= most)
                continue;
            if (column == used.Count)
                used.Add([]);
            used[column].Add((low, high));
            columns[index] = column;
        }
        return (columns, used.Count);
    }
}
