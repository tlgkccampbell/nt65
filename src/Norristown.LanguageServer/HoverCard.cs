namespace Norristown.LanguageServer;

/// <summary>
/// Builds one hover's text, in the order it is read. The text is the line under the caret in
/// the language's own syntax, the comment its author left above it, the facts that kind of
/// thing is asked about most, a rule, and everything else the analysis worked out under it. A
/// zone with nothing in it is left out, and where nothing leads there is no rule either.
/// <para>
/// The caller decides which keys lead. Rows keep the order they were added in on both sides
/// of the rule, so a fact appears in the same relative place from one hover to the next. Both
/// grids pad their keys to the same width, so the values line up across the rule.
/// </para>
/// <para>
/// A row whose fact is not known is left out rather than shown as unknown, so the grid holds
/// only what is known.
/// </para>
/// </summary>
/// <param name="headline">The line under the caret, in the language's syntax.</param>
/// <param name="asked">The keys of the rows that stand above the rule.</param>
internal sealed class HoverCard(string headline, IReadOnlySet<string> asked)
{
    /// <summary>The number of spaces that separate the longest key from the values.</summary>
    private const int Gutter = 2;

    /// <summary>
    /// The language tag on the fenced code block that holds a hover's key/value grid. Markdown
    /// formatting does not apply inside a fenced block, so the only way to style the grid's
    /// parts is to give it a grammar of its own. An editor without that grammar renders it as
    /// plain monospace.
    /// </summary>
    private const string Grid = "nt65-hover";

    /// <summary>
    /// The rows, each marked with whether it leads, where a null row stands for a blank line
    /// between two groups of rows.
    /// </summary>
    private readonly List<((string Key, string Value)? Row, bool Leads)> rows = [];

    private string? prose;

    /// <summary>
    /// Whether the last row added leads. A following row with an empty key inherits this.
    /// </summary>
    private bool leading;

    /// <summary>
    /// Adds one row to the grid, or nothing when there is nothing to say. A row with an empty
    /// key continues the row above it, which is how one fact spans several lines.
    /// </summary>
    public void Row(string key, string? value)
    {
        if (value is not { Length: > 0 })
            return;
        if (key.Length > 0)
            leading = asked.Contains(key);
        rows.Add(((key, value), leading));
    }

    /// <summary>
    /// Adds a blank line between two groups of rows, which keeps them in the same columns.
    /// </summary>
    public void Gap() => rows.Add((null, leading));

    /// <summary>Sets the comment above the declaration, which is what its author had to say.</summary>
    public void Prose(string? comment) => prose = comment;

    /// <inheritdoc/>
    public override string ToString()
    {
        var column = (rows.Count == 0 ? 0 : rows.Max(row => row.Row?.Key.Length ?? 0)) + Gutter;

        // A gap before the first row of a group would open the grid with a blank line, and
        // one after the last would close it with one.
        var lead = Trimmed(rows.Where(row => row.Leads));
        var rest = Trimmed(rows.Where(row => !row.Leads));

        // The headline, the comment and the leading rows stay together; the single rule
        // separates them from the supporting detail below.
        var above = new List<string>();
        if (headline.Length > 0)
            above.Add($"```nt65\n{headline}\n```");
        if (prose is { Length: > 0 })
            above.Add(prose);
        if (lead.Count > 0)
            above.Add(Table(lead, column));
        // The rule separates the leading rows from the rest, so without leading rows there is
        // nothing for it to separate, and the rest follows as the leading rows would.
        if (lead.Count == 0)
            return string.Join("\n\n", rest.Count == 0 ? above : [.. above, Table(rest, column)]);
        var answer = string.Join("\n\n", above);
        return rest.Count == 0 ? answer : $"{answer}\n---\n{Table(rest, column)}";
    }

    private static IReadOnlyList<(string Key, string Value)?> Trimmed(
        IEnumerable<((string Key, string Value)? Row, bool Leads)> group)
    {
        var rows = group.Select(row => row.Row).ToList();
        while (rows.Count > 0 && rows[0] is null)
            rows.RemoveAt(0);
        while (rows.Count > 0 && rows[^1] is null)
            rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private static string Table(IReadOnlyList<(string Key, string Value)?> rows, int column) =>
        $"```{Grid}\n"
            + string.Join("\n", rows.Select(row => row is { } has ? has.Key.PadRight(column) + has.Value : ""))
            + "\n```";
}
