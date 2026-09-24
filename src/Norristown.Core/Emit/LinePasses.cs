using System.Text;
using Norristown.Layout;
using static Norristown.Emit.Ca65Numbers;

namespace Norristown.Emit;

/// <summary>
/// Rewrites runs of the emitter's lines once they are written. Each pass works on the
/// <see cref="EmittedLine"/> list alone, because what it decides depends on a whole run of lines
/// rather than on the statement that wrote any one of them.
/// </summary>
internal static class LinePasses
{
    /// <summary>The fewest equal lines that <see cref="FoldFills"/> rewrites as one <c>.res</c>.</summary>
    private const int MinimumFill = 3;

    /// <summary>
    /// The fewest iterations that are written as a <c>.repeat</c>. Two iterations read no better
    /// as a <c>.repeat</c> than written out, and one reads worse.
    /// </summary>
    private const int MinimumRepeat = 3;

    /// <summary>
    /// Lines up the directives of each run of named data lines, so that a run reads as a column
    /// the way hand-written ca65 does. The names are nt65's output names, not the source's, so
    /// the source's alignment no longer holds, and the column can only be chosen once the whole
    /// run is known.
    /// </summary>
    public static void AlignColumns(List<EmittedLine> lines)
    {
        foreach (var run in NamedRuns(lines))
        {
            var column = run.Max(at => lines[at].Label!.Length) + 1;
            foreach (var at in run)
            {
                var line = lines[at];
                var label = line.Label!;
                lines[at] = line with
                {
                    Text = label + new string(' ', column - label.Length) + line.Text,
                    Label = null,
                    Kind = EmittedLineKind.Declaration,
                    Value = null,
                };
            }
        }
    }

    /// <summary>
    /// Rewrites each run of lines holding one repeated byte as a single <c>.res</c> that
    /// produces the same bytes. A repetition of a single value unrolls to a row of equal lines,
    /// which is a fill regardless of how the source expressed it, and ca65 expresses a fill as
    /// <c>.res n, value</c>.
    /// <para>
    /// This is done last, because it removes lines. The line that replaces a run keeps the source
    /// line of the run's first line and the byte count of the whole run.
    /// </para>
    /// </summary>
    public static void FoldFills(List<EmittedLine> lines)
    {
        var kept = new List<EmittedLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            // A run longer than one `.res` reserves is left as it is. Gathering it would need
            // several directives, and a file with that many equal lines in it is a repetition
            // nobody would have written by hand either.
            var run = 1;
            while (i + run < lines.Count && run < DataLengths.MaxReservation && Same(lines[i + run], lines[i]) && RepeatedByte(lines[i]) is not null)
                run++;
            if (run >= MinimumFill && RepeatedByte(lines[i]) is { } value)
            {
                var indent = lines[i].Text[..(lines[i].Text.Length - lines[i].Text.TrimStart().Length)];
                kept.Add(lines[i] with
                {
                    Text = $"{indent}.res {run}, {value}",
                    Bytes = run * lines[i].Bytes,
                    Kind = EmittedLineKind.Other,
                    Value = null,
                });
                i += run - 1;
                continue;
            }
            kept.Add(lines[i]);
        }
        lines.Clear();
        lines.AddRange(kept);
    }

    /// <summary>
    /// Rewrites a counted repetition whose iterations all came out the same as a single ca65
    /// <c>.repeat</c> that produces the same lines. nt65 makes every per-iteration decision
    /// itself, such as the address size an operand is reached at, the width an immediate is
    /// written at, the form a branch takes and the name an iteration declares. The iterations
    /// are therefore written first and compared afterwards, and ca65 is given a <c>.repeat</c>
    /// only when there is nothing left for it to decide.
    /// <para>
    /// Where the iterations differ only in the value of the binding, the body is written once
    /// with a ca65 repeat counter in place of that number. That happens only after substituting
    /// each iteration's number back into it has reproduced that iteration's lines exactly.
    /// </para>
    /// <para>
    /// The byte count of the whole block goes on the <c>.repeat</c> line, because that is the
    /// line ca65 counts every iteration's bytes against. The body's lines are given no bytes of
    /// their own, and keep only their source line.
    /// </para>
    /// </summary>
    /// <param name="lines">The lines written so far, which end with the repetition's iterations.</param>
    /// <param name="starts">Where each iteration's lines begin.</param>
    /// <param name="counterName">
    /// Returns the name of the repetition's counter, which is asked for only once a counter is
    /// known to reproduce every iteration. Null for a repetition with no binding.
    /// </param>
    /// <param name="opens">The source line the <c>.repeat</c> is mapped to when it holds bytes.</param>
    /// <param name="closes">The source line the <c>.endrepeat</c> is mapped to.</param>
    /// <param name="file">The index of the translation unit's source the lines count in.</param>
    public static void FoldRepeat(
        List<EmittedLine> lines, IReadOnlyList<int> starts, Func<string>? counterName, int opens, int closes, int file)
    {
        if (Iterations(lines, starts, out var at) is not { } iterations)
            return;

        var body = iterations[^1];
        var counter = "";
        if (!iterations.TrueForAll(iteration => Identical(iteration, body)))
        {
            if (counterName is null || CountedBody(iterations, counterName) is not { } counted)
                return;
            (counter, body) = ($", {counted.Counter}", counted.Body);
        }
        else if (RepeatedByte(body[0]) is not null && body.TrueForAll(line => Same(line, body[0])))
        {
            // A row of one repeated byte is a fill regardless of how the source expressed it, and
            // `.res` says a fill more plainly than a `.repeat` around one `.byte` does.
            return;
        }

        // A line whose length nt65 makes no claim about makes the whole block's length
        // unpredictable too, because the iterations' total is as unpredictable as the line.
        long bytes = 0;
        foreach (var line in body)
            bytes = line.Bytes < 0 || bytes < 0 ? DataLengths.Unpredictable : bytes + line.Bytes;
        bytes = bytes <= 0 ? bytes : bytes * starts.Count;
        var length = bytes is > 0 and <= int.MaxValue ? (int)bytes : bytes < 0 ? DataLengths.Unpredictable : 0;

        var opened = body.Select(line => line.Text).FirstOrDefault(text => text.Length > 0) ?? Emitter.Body;
        var indent = opened[..(opened.Length - opened.TrimStart().Length)];
        lines.RemoveRange(at, lines.Count - at);
        Add(lines, new EmittedLine($"{indent}.repeat {starts.Count}{counter}", length, length != 0 ? opens : 0), file);
        foreach (var line in body)
            Add(lines, line with { Text = line.Text.Length == 0 ? "" : Emitter.Body + line.Text, Bytes = 0 }, file);

        // ca65 counts the block's bytes against the `.repeat`, and ld65 records a span for the
        // whole of it against the `.endrepeat`, so the closing line is mapped as well as the
        // opening one. The map has to cover every line the debug information refers to.
        Add(lines, new EmittedLine($"{indent}.endrepeat", Source: closes), file);
    }

    /// <summary>Appends a line as the emitter appends every line, trimmed and recording its source file.</summary>
    private static void Add(List<EmittedLine> lines, EmittedLine line, int file) =>
        lines.Add(line with { Text = line.Text.TrimEnd(), File = file });

    /// <summary>
    /// Returns the lines each iteration came out as, or null when no <c>.repeat</c> could
    /// represent the block, no matter how alike the iterations are. <paramref name="at"/> is
    /// where the <c>.repeat</c> goes. The first iteration may begin with lines that belong before
    /// the repetition rather than inside it, such as a blank the source asked for or the
    /// <c>.segment</c> that the following code lands in. Later iterations did not need to write
    /// those lines again, and they stay where they are.
    /// </summary>
    private static List<List<EmittedLine>>? Iterations(List<EmittedLine> lines, IReadOnlyList<int> starts, out int at)
    {
        at = lines.Count;
        if (starts.Count < MinimumRepeat)
            return null;

        var length = lines.Count - starts[^1];
        if (length == 0)
            return null;
        for (var iteration = 1; iteration < starts.Count; iteration++)
        {
            var end = iteration + 1 < starts.Count ? starts[iteration + 1] : lines.Count;
            if (end - starts[iteration] != length)
                return null;
        }

        var opening = starts[1] - starts[0] - length;
        if (opening < 0)
            return null;
        for (var i = 0; i < opening; i++)
        {
            if (lines[starts[0] + i] is { Text.Length: not 0, Kind: not EmittedLineKind.Segment })
                return null;
        }

        var iterations = new List<List<EmittedLine>>(starts.Count) { lines.GetRange(starts[0] + opening, length) };
        for (var iteration = 1; iteration < starts.Count; iteration++)
            iterations.Add(lines.GetRange(starts[iteration], length));

        // A name inside a ca65 `.repeat` is declared once per iteration, which is an error on
        // the second, so such a body is written out in full no matter how alike the iterations
        // look.
        if (iterations[0].Any(Declares))
            return null;

        at = starts[0] + opening;
        return iterations;
    }

    /// <summary>Returns whether two iterations came out as exactly the same lines.</summary>
    private static bool Identical(List<EmittedLine> one, List<EmittedLine> other)
    {
        for (var i = 0; i < one.Count; i++)
        {
            if (one[i] != other[i])
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the single body every iteration of a counted repetition shares, expressed in terms
    /// of a ca65 repeat counter, together with the counter's name. Returns null when no such body
    /// reproduces every iteration. ca65 substitutes the iteration number wherever the counter's
    /// name appears, so the body is the first iteration with the counter in place of its number.
    /// It is used only if substituting each iteration's number back reproduces that iteration's
    /// lines exactly. ca65 then evaluates the source's expression, in arithmetic nt65 already
    /// agrees with it on, since each line is the line nt65 wrote for that iteration with one
    /// number substituted.
    /// </summary>
    private static (string Counter, List<EmittedLine> Body)? CountedBody(
        List<List<EmittedLine>> iterations, Func<string> counterName)
    {
        // A character no output contains, so that the places the counter goes can be marked
        // before the counter has a name.
        const string mark = "\u0001";

        var body = new List<EmittedLine>(iterations[0].Count);
        for (var i = 0; i < iterations[0].Count; i++)
        {
            var line = iterations[0][i];
            if (!iterations.TrueForAll(iteration => Alongside(iteration[i], line))
                || Templated(line.Text, iterations[1][i].Text, mark) is not { } text)
            {
                return null;
            }

            // A byte's value is part of the line's text, so it takes the counter wherever the
            // text does.
            var value = line.Value is null ? null : Templated(line.Value, iterations[1][i].Value ?? "", mark);
            if (line.Value is not null && value is null)
                return null;
            body.Add(line with { Text = text, Value = value });
        }
        for (var iteration = 0; iteration < iterations.Count; iteration++)
        {
            for (var i = 0; i < body.Count; i++)
            {
                if (Instantiated(body[i].Text, mark, Constant(iteration)) != iterations[iteration][i].Text)
                    return null;
            }
        }

        var counter = counterName();
        return (counter, [.. body.Select(line => line with
        {
            Text = line.Text.Replace(mark, counter, StringComparison.Ordinal),
            Value = line.Value?.Replace(mark, counter, StringComparison.Ordinal),
        })]);
    }

    /// <summary>
    /// Returns the line the first two iterations wrote, with <paramref name="mark"/> wherever the
    /// first wrote the binding's value on its iteration (0) and the second wrote its own (1).
    /// Returns null when they differ anywhere else, which means some decision came out
    /// differently and no counter can represent it.
    /// </summary>
    private static string? Templated(string zeroth, string first, string mark)
    {
        if (zeroth.Length != first.Length)
            return null;
        var zero = Constant(0);
        var one = Constant(1);
        var text = new StringBuilder(zeroth.Length);
        for (var at = 0; at < zeroth.Length;)
        {
            if (at + zero.Length <= zeroth.Length
                && string.CompareOrdinal(zeroth, at, zero, 0, zero.Length) == 0
                && string.CompareOrdinal(first, at, one, 0, one.Length) == 0
                && (at == 0 || !InAName(zeroth[at - 1]))
                && (at + zero.Length == zeroth.Length || !InAName(zeroth[at + zero.Length])))
            {
                text.Append(mark);
                at += zero.Length;
                continue;
            }
            if (zeroth[at] != first[at])
                return null;
            text.Append(zeroth[at]);
            at++;
        }
        return text.ToString();
    }

    /// <summary>
    /// Returns <paramref name="body"/> with <paramref name="value"/> substituted for
    /// <paramref name="mark"/>, the way ca65 substitutes the iteration number for the counter's
    /// name. The value replaces the mark only where the mark stands as a whole word, never inside
    /// a longer one.
    /// </summary>
    private static string Instantiated(string body, string mark, string value)
    {
        var text = new StringBuilder(body.Length);
        for (var at = 0; at < body.Length;)
        {
            var found = body.IndexOf(mark, at, StringComparison.Ordinal);
            if (found < 0)
            {
                text.Append(body, at, body.Length - at);
                break;
            }
            text.Append(body, at, found - at);
            var after = found + mark.Length;
            text.Append((found == 0 || !InAName(body[found - 1]))
                && (after == body.Length || !InAName(body[after])) ? value : mark);
            at = after;
        }
        return text.ToString();
    }

    /// <summary>
    /// Returns whether a character can appear in a ca65 name, or is the dot that begins a ca65
    /// directive.
    /// </summary>
    private static bool InAName(char letter) => char.IsLetterOrDigit(letter) || letter == '_' || letter == '.';

    /// <summary>
    /// Returns whether two lines agree in everything but their text and the value it spells, which
    /// may differ by the repetition's counter.
    /// </summary>
    private static bool Alongside(EmittedLine one, EmittedLine other) =>
        one.Bytes == other.Bytes && one.Source == other.Source && one.Kind == other.Kind
            && one.Label == other.Label && one.Comment == other.Comment;

    /// <summary>
    /// Returns whether a line declares a name. A name declared in a repetition's body is a different
    /// name on every iteration, and a ca65 <c>.repeat</c> has no way to express that.
    /// </summary>
    private static bool Declares(EmittedLine line) => line.Label is not null || line.Kind == EmittedLineKind.Declaration;

    /// <summary>
    /// Returns whether two lines have the same text, label and comment. Their byte counts and
    /// source lines are ignored, because a row of equal bytes is a fill no matter how many source
    /// lines produced it, and the fill is mapped to the first of them.
    /// </summary>
    private static bool Same(EmittedLine a, EmittedLine b) =>
        a.Text == b.Text && a.Label == b.Label && a.Comment == b.Comment;

    /// <summary>
    /// Returns the value of the single byte a line writes, or null for a line that writes
    /// anything else or carries a name, because only such a line can be one byte of a fill. The
    /// line's comment is kept on the fill, because what it says about the byte is equally true of
    /// the fill the run becomes.
    /// </summary>
    private static string? RepeatedByte(EmittedLine line) =>
        line is { Kind: EmittedLineKind.Byte, Label: null } ? line.Value : null;

    /// <summary>Returns the runs of lines to line up, which are named data lines with nothing in between.</summary>
    private static List<List<int>> NamedRuns(List<EmittedLine> lines)
    {
        var runs = new List<List<int>>();
        for (var at = 0; at < lines.Count; at++)
        {
            if (lines[at].Label is null)
                continue;
            if (runs.Count > 0 && runs[^1][^1] == at - 1)
                runs[^1].Add(at);
            else
                runs.Add([at]);
        }
        return runs;
    }
}
