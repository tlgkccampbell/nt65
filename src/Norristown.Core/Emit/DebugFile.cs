using System.Globalization;
using System.Text;

namespace Norristown.Emit;

/// <summary>
/// Copies what the line maps say into ld65's debug file after the link; this is what
/// <c>nt65 remap-dbg</c> does.
/// <para>
/// <c>ca65 -g</c> records the lines of the <c>.s</c> it assembles, and ld65 copies them into the
/// file <c>--dbgfile</c> names. nt65 writes no <c>.dbg</c> directives, so the debug file that
/// arrives here refers only to the generated ca65; this adds what the <c>.s.lines</c> files
/// beside those <c>.s</c> files say, so that it also refers to the <c>.nt65</c> the person wrote.
/// </para>
/// <para>
/// Almost everything is added rather than changed: a <c>file</c> record for each source, a
/// <c>line</c> record for each of its lines, and the new line id on each <c>sym</c> that was
/// defined or used at a mapped line. The existing records stay, so a debugger that was showing
/// the generated ca65 still can. The one change is a <c>mod</c> record's <c>file</c>, which is
/// set to the source the module was written from, because a module names only one file and the
/// source is the one worth naming. A <c>.s</c> with no map beside it — anything hand-written
/// that the same program links — is left alone, which is also why running this a second time
/// changes nothing.
/// </para>
/// </summary>
public static class DebugFile
{
    /// <summary>What ld65 writes on the first line of a debug file it produces.</summary>
    private const string Version = "version\tmajor=2,minor=0";

    /// <summary>
    /// <paramref name="text"/> with every mapped <c>.s</c> line also named as the source line it
    /// came from, or null with <paramref name="problem"/> saying what is wrong.
    /// <paramref name="map"/> returns the text of the line map beside the <c>.s</c> at the path it
    /// is given, or null when there is none.
    /// </summary>
    public static string? Remap(string text, Func<string, string?> map, out string? problem)
    {
        problem = null;
        var records = text.Split('\n').Select(Record.Parse).ToList();
        if (!records.Any(record => record.Line.TrimEnd('\r') == Version))
        {
            problem = "it is not a version 2.0 ld65 debug file, the kind ld65 writes with `--dbgfile`";
            return null;
        }

        var files = records.Where(record => record.Keyword == "file").ToList();
        var named = files.Select(record => record["name"] ?? "").ToHashSet(StringComparer.Ordinal);

        // ld65 numbers each kind of record from zero and gives the count in its `info` record,
        // and the two agree. New ids start past both the count and the highest id in use, so
        // that even a file where they disagreed would not have an id given out twice.
        var nextFile = Next(records, "file", files);
        var nextLine = Next(records, "line", records.Where(record => record.Keyword == "line"));

        // For each `.s` file record that has a line map: the map, and the new ids given to the
        // sources it names.
        var sources = new Dictionary<int, (SourceLines Map, int[] Ids)>();
        var added = new List<string>();
        foreach (var record in files)
        {
            if (record["name"] is not { } path || Number(record["id"]) is not { } id)
                continue;
            if (map(path) is not { } beside)
                continue;
            if (LineMap.Read(beside, out var wrong) is not { } lines)
            {
                problem = $"cannot read {path}{LineMap.Extension}, the line map nt65 wrote for {path}: {wrong}";
                return null;
            }

            // A source this debug file already names is one a previous run added.
            if (lines.Sources.Any(source => named.Contains(source.Path)))
                continue;
            var ids = new int[lines.Sources.Count];
            for (var i = 0; i < lines.Sources.Count; i++)
            {
                var (source, size) = lines.Sources[i];
                ids[i] = nextFile++;
                named.Add(source);
                added.Add($"file\tid={ids[i]},name=\"{source}\",size={size},mtime=0x00000000,mod={record["mod"]}");
            }
            sources[id] = (lines, ids);
        }
        if (sources.Count == 0)
            return text;

        // One new line record per source line, however many lines of the `.s` came from it, with
        // the spans of all of them: ld65 attaches a span to whichever line was in effect, and two
        // records for one source line would only say the same thing twice.
        var merged = new Dictionary<(int File, int Line), (int Id, List<string> Spans)>();
        var replaced = new Dictionary<int, int>();
        foreach (var record in records.Where(record => record.Keyword == "line"))
        {
            if (Number(record["file"]) is not { } file || !sources.TryGetValue(file, out var source))
                continue;
            if (Number(record["id"]) is not { } id || Number(record["line"]) is not { } at)
                continue;
            if (!source.Map.Lines.TryGetValue(at, out var from))
                continue;
            var key = (source.Ids[from.File], from.Line);
            if (!merged.TryGetValue(key, out var entry))
                merged[key] = entry = (nextLine++, []);
            if (record["span"] is { Length: > 0 } spans)
                entry.Spans.AddRange(spans.Split('+'));
            replaced[id] = entry.Id;
        }

        var lineRecords = merged
            .OrderBy(entry => entry.Value.Id)
            .Select(entry => $"line\tid={entry.Value.Id},file={entry.Key.File},line={entry.Key.Line},type=1"
                + Spanned(entry.Value.Spans))
            .ToList();

        // ld65 writes the file in text mode, so on Windows it arrives with CRLF; the result is
        // written with the same line endings, even though the tools that read it hardly care.
        var written = Written(records, sources, replaced, added, lineRecords);
        return text.Contains("\r\n", StringComparison.Ordinal)
            ? written.Replace("\n", "\r\n", StringComparison.Ordinal) : written;
    }

    /// <summary>The debug file with the added records in place and the counts to match.</summary>
    private static string Written(
        IReadOnlyList<Record> records, IReadOnlyDictionary<int, (SourceLines Map, int[] Ids)> sources,
        IReadOnlyDictionary<int, int> replaced, IReadOnlyList<string> files, IReadOnlyList<string> lines)
    {
        var lastFile = Last(records, "file");
        var lastLine = Last(records, "line");
        var text = new StringBuilder();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            text.Append(Rewritten(record, sources, replaced, files.Count, lines.Count)).Append('\n');
            if (i == lastFile)
            {
                foreach (var added in files)
                    text.Append(added).Append('\n');
            }
            if (i == lastLine)
            {
                foreach (var added in lines)
                    text.Append(added).Append('\n');
            }
        }

        // The split leaves an empty last record for the newline ld65 ends the file with.
        return text.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>One record as it is written back; most records are written back unchanged.</summary>
    private static string Rewritten(
        Record record, IReadOnlyDictionary<int, (SourceLines Map, int[] Ids)> sources,
        IReadOnlyDictionary<int, int> replaced, int files, int lines)
    {
        switch (record.Keyword)
        {
            // The counts let a debugger set aside the memory it will need before it reads.
            case "info":
                return record.With(("file", field => Count(field, files)), ("line", field => Count(field, lines)));

            // A module names one file, and the one worth naming is what it was written from.
            case "mod" when Number(record["file"]) is { } file && sources.TryGetValue(file, out var source):
                return record.With(("file", _ => source.Ids[0].ToString(CultureInfo.InvariantCulture)));

            // A symbol is defined and used at the source lines as well as the generated ones.
            case "sym":
                return record.With(
                    ("def", field => Also(field, replaced)), ("ref", field => Also(field, replaced)));
            default:
                return record.Line;
        }
    }

    /// <summary>A <c>+</c>-separated list of line ids, with the ones they were mapped to added.</summary>
    private static string Also(string field, IReadOnlyDictionary<int, int> replaced)
    {
        var ids = field.Split('+').Select(id => Number(id) ?? -1).ToList();
        var also = ids.Select(id => replaced.GetValueOrDefault(id, -1)).Where(id => id >= 0 && !ids.Contains(id));
        return string.Join('+', ids.Concat(also).Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>A count field with <paramref name="more"/> added to it.</summary>
    private static string Count(string field, int more) =>
        ((Number(field) ?? 0) + more).ToString(CultureInfo.InvariantCulture);

    /// <summary>The <c>span</c> field of a line record, or nothing when it covers no bytes.</summary>
    private static string Spanned(IReadOnlyList<string> spans) =>
        spans.Count == 0 ? "" : ",span=" + string.Join('+', spans.Distinct(StringComparer.Ordinal));

    /// <summary>
    /// The first unused id for <paramref name="keyword"/> records: past every id in
    /// <paramref name="of"/>, and no lower than the count the <c>info</c> record gives.
    /// </summary>
    private static int Next(IReadOnlyList<Record> records, string keyword, IEnumerable<Record> of)
    {
        var counted = Number(records.FirstOrDefault(record => record.Keyword == "info")?[keyword]) ?? 0;
        return of.Aggregate(counted, (next, record) => Math.Max(next, (Number(record["id"]) ?? -1) + 1));
    }

    /// <summary>The index of the last <paramref name="keyword"/> record, or of the last record when there is none.</summary>
    private static int Last(IReadOnlyList<Record> records, string keyword)
    {
        for (var i = records.Count - 1; i >= 0; i--)
        {
            if (records[i].Keyword == keyword)
                return i;
        }
        return records.Count - 1;
    }

    /// <summary>A field as a number, or null when it is not one.</summary>
    private static int? Number(string? field) =>
        field is not null && int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    /// <summary>
    /// One line of a debug file: a keyword and a tab, then <c>name=value</c> fields separated by
    /// commas, where a value may be quoted and so may itself hold a comma.
    /// </summary>
    private sealed class Record
    {
        private readonly List<(string Name, string Value)> fields = [];

        private Record(string line, string keyword)
        {
            Line = line;
            Keyword = keyword;
        }

        /// <summary>The line as it arrived, which is what is written back for all but a few.</summary>
        public string Line { get; }

        /// <summary>What the line is about: <c>file</c>, <c>line</c>, <c>sym</c> and the rest.</summary>
        public string Keyword { get; }

        public string? this[string name] =>
            fields.FirstOrDefault(field => field.Name == name) is { Name.Length: > 0 } found
                ? found.Value.Trim('"') : null;

        public static Record Parse(string line)
        {
            var body = line.TrimEnd('\r');
            var at = body.IndexOf('\t');
            var record = new Record(body, at < 0 ? body : body[..at]);
            if (at < 0)
                return record;
            foreach (var field in Split(body[(at + 1)..]))
            {
                var equals = field.IndexOf('=');
                if (equals > 0)
                    record.fields.Add((field[..equals], field[(equals + 1)..]));
            }
            return record;
        }

        /// <summary>The record with <paramref name="changes"/> applied to the fields they name.</summary>
        public string With(params (string Name, Func<string, string> Change)[] changes)
        {
            var text = new StringBuilder(Keyword).Append('\t');
            for (var i = 0; i < fields.Count; i++)
            {
                var (name, value) = fields[i];
                var change = changes.FirstOrDefault(change => change.Name == name).Change;
                text.Append(i == 0 ? "" : ",").Append(name).Append('=')
                    .Append(change is null ? value : change(value.Trim('"')));
            }
            return text.ToString();
        }

        /// <summary>The comma-separated fields of a record, keeping a comma inside quotes.</summary>
        private static IEnumerable<string> Split(string body)
        {
            var start = 0;
            var quoted = false;
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '"')
                    quoted = !quoted;
                else if (body[i] == ',' && !quoted)
                {
                    yield return body[start..i];
                    start = i + 1;
                }
            }
            yield return body[start..];
        }
    }
}
