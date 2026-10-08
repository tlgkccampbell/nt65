using Norristown.Flow;
using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the <c>nt65/flowArrows</c> request. It converts what <see cref="FlowArrows"/> finds for
/// the caret into lines, and gives each arrow the column its upright is drawn in.
/// </summary>
internal static class CaretFlow
{
    /// <summary>
    /// The most columns the arrows take. Every column is paid for on every line of the routine,
    /// because the client draws them as a prefix that shifts the code right.
    /// </summary>
    public const int MostColumns = 4;

    /// <summary>
    /// Returns the arrows of the routine that holds <paramref name="position"/>, or null where no
    /// routine holds it or the routine transfers control nowhere.
    /// </summary>
    public static Protocol.FlowArrowsResult? At(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        if (FlowArrows.At(analysis, model, position) is not { Arrows.Count: > 0 } found)
            return null;
        var tree = model.Tree;
        var lines = found.Arrows
            .Select(arrow => (From: tree.GetLineIndex(arrow.From.Start), To: arrow.To is { } to ? tree.GetLineIndex(to.Start) : (int?)null))
            .ToList();
        var (columns, count) = Columns(lines);
        return new Protocol.FlowArrowsResult(
            tree.GetLineIndex(found.Routine.Start),
            tree.GetLineIndex(Math.Max(found.Routine.Start, found.Routine.End - 1)),
            count,
            [.. found.Arrows.Select((arrow, index) => new Protocol.FlowArrowsItem(
                lines[index].From, lines[index].To, columns[index], arrow.IsDeclared, arrow.IsProved, arrow.IsReached))]);
    }

    /// <summary>
    /// Returns the column each arrow's upright goes in, and how many columns there are. Shorter
    /// arrows go nearer the code, so that an arrow inside another is drawn inside it. Two arrows
    /// share a column only where their lines do not overlap, counting the lines at their ends.
    /// </summary>
    /// <param name="arrows">The line each arrow starts on, and the line it ends on or null where it leaves the routine.</param>
    private static (int?[] Columns, int Count) Columns(IReadOnlyList<(int From, int? To)> arrows)
    {
        var columns = new int?[arrows.Count];
        var used = new List<List<(int Low, int High)>>();
        var order = Enumerable.Range(0, arrows.Count)
            .Where(index => arrows[index].To is not null)
            .Select(index => (Index: index, Low: Math.Min(arrows[index].From, arrows[index].To!.Value), High: Math.Max(arrows[index].From, arrows[index].To!.Value)))
            .OrderBy(arrow => arrow.High - arrow.Low)
            .ThenBy(arrow => arrow.Low);
        foreach (var (index, low, high) in order)
        {
            var column = 0;
            while (column < used.Count && used[column].Any(span => span.Low <= high && low <= span.High))
                column++;
            if (column >= MostColumns)
                continue;
            if (column == used.Count)
                used.Add([]);
            used[column].Add((low, high));
            columns[index] = column;
        }
        return (columns, used.Count);
    }
}
