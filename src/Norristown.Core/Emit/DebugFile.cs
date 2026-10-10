using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Norristown.Emit;

/// <summary>
/// Copies the contents of the line maps into ld65's debug file after the link, which is what
/// <c>nt65 remap-dbg</c> does.
/// <para>
/// <c>ca65 -g</c> records the lines of the <c>.s</c> it assembles, and ld65 copies them into the
/// file that <c>--dbgfile</c> names. nt65 emits no <c>.dbg</c> directives, so the debug file
/// that arrives here refers only to the generated ca65. This class adds the contents of the
/// <c>.s.lines</c> files beside those <c>.s</c> files, so that the debug file also refers to the
/// <c>.nt65</c> source the person maintains.
/// </para>
/// <para>
/// Almost everything is added rather than changed. The additions are a <c>file</c> record for
/// each source, a <c>line</c> record for each of its lines, and the new line id on each
/// <c>sym</c> that was defined or used at a mapped line. The existing records stay, so a
/// debugger that was showing the generated ca65 still can. The one change is a <c>mod</c>
/// record's <c>file</c>, which is set to the source the module was compiled from, because a
/// module names only one file and the source is the one worth naming. A <c>.s</c> with no map
/// beside it, such as hand-written ca65 that the same program links, is left alone, which is
/// also why running this a second time changes nothing.
/// </para>
/// <para>
/// The same maps also name each value the program defines by its source path, from which
/// <see cref="TryLabels"/> builds a label file for <c>nt65 remap-dbg --labels</c>.
/// </para>
/// </summary>
public static class DebugFile
{
    /// <summary>What ld65 writes on the first line of a debug file it produces.</summary>
    private const string Version = "version\tmajor=2,minor=0";

    /// <summary>
    /// Names every mapped <c>.s</c> line of a debug file also as the source line it came from.
    /// </summary>
    /// <param name="text">The text of the debug file.</param>
    /// <param name="map">
    /// The function that returns the text of the line map beside the <c>.s</c> at the path it is
    /// given, or null when there is none.
    /// </param>
    /// <param name="remapped">The remapped debug file, or null when it could not be remapped.</param>
    /// <param name="problem">What is wrong, or null when the debug file was remapped.</param>
    /// <returns>True if the debug file was remapped.</returns>
    public static bool TryRemap(
        string text, Func<string, string?> map,
        [NotNullWhen(true)] out string? remapped, [NotNullWhen(false)] out string? problem)
    {
        remapped = null;
        if (!TryParse(text, out var records, out problem))
            return false;

        var files = records.Where(record => record.Keyword == "file").ToList();
        var named = files.Select(record => record["name"] ?? "").ToHashSet(StringComparer.Ordinal);

        // ld65 numbers each kind of record from zero and gives the count in its `info` record,
        // and the two agree. New ids start past both the count and the highest id in use, so
        // that even a file where they disagreed would not have an id given out twice.
        var nextFile = Next(records, "file", files);
        var nextLine = Next(records, "line", records.Where(record => record.Keyword == "line"));

        // For each `.s` file record that has a line map, the map and the new ids given to the
        // sources it names.
        var sources = new Dictionary<int, (SourceLines Map, int[] Ids)>();
        var added = new List<string>();
        foreach (var record in files)
        {
            if (record["name"] is not { } path || Number(record["id"]) is not { } id)
                continue;
            if (map(path) is not { } beside)
                continue;
            if (!LineMap.TryRead(beside, out var lines, out var wrong))
            {
                problem = $"cannot read {path}{LineMap.Extension}, the line map nt65 wrote for {path}: {wrong}";
                return false;
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
        {
            remapped = text;
            return true;
        }

        // There is one new line record per source line, regardless of how many lines of the `.s`
        // came from it, with the spans of all of them. ld65 attaches a span to the line in effect,
        // and two records for one source line would only say the same thing twice.
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

        // ld65 writes the file in text mode, so on Windows it arrives with CRLF. The result is
        // written with the same line endings, even though the tools that read it hardly care.
        var written = Written(records, sources, replaced, added, lineRecords);
        remapped = text.Contains("\r\n", StringComparison.Ordinal)
            ? written.Replace("\n", "\r\n", StringComparison.Ordinal) : written;
        return true;
    }

    /// <summary>
    /// Builds a label file from a debug file, in the form ld65 writes with <c>-Ln</c> and VICE
    /// reads, with each value named by its source path, such as <c>wave::shown</c>.
    /// <para>
    /// ld65's own label file names a value the way the <c>.s</c> does. That is the linker name for
    /// an export, but a name that is not exported keeps its spelling within its module, so two
    /// modules' private names can be the same. The line maps give each name its path, which no two
    /// values share. A name the maps do not give, such as a cheap local, is left out. A module
    /// with no map, such as hand-written ca65, contributes its labels under their own names.
    /// </para>
    /// </summary>
    /// <param name="text">The text of the debug file.</param>
    /// <param name="map">
    /// The function that returns the text of the line map beside the <c>.s</c> at the path it is
    /// given, or null when there is none.
    /// </param>
    /// <param name="labels">The label file, or null when it could not be built.</param>
    /// <param name="problem">What is wrong, or null when the label file was built.</param>
    /// <returns>True if the label file was built.</returns>
    public static bool TryLabels(
        string text, Func<string, string?> map,
        [NotNullWhen(true)] out string? labels, [NotNullWhen(false)] out string? problem)
    {
        labels = null;
        if (!TryValues(text, map, out var values, out problem))
            return false;

        var found = new SortedSet<(long Value, string Name)>(Comparer<(long Value, string Name)>.Create((a, b) =>
            a.Value != b.Value ? a.Value.CompareTo(b.Value) : string.CompareOrdinal(a.Name, b.Name)));
        foreach (var (name, type, value, lines) in values)
        {
            if (lines is not null)
            {
                if (lines.Names.TryGetValue(name, out var path))
                    found.Add((value, path));
            }
            else if (type == "lab")
            {
                found.Add((value, name));
            }
        }

        var written = new StringBuilder();
        foreach (var (value, name) in found)
            written.Append(CultureInfo.InvariantCulture, $"al {value:X6} .{name}\n");
        labels = written.ToString();
        return true;
    }

    /// <summary>
    /// Reads every value a debug file gives, except the imports, with the line map of the module
    /// that defines it. A value from a module assembled from a <c>.s</c> with no map, such as
    /// hand-written ca65, comes with no map.
    /// </summary>
    /// <param name="text">The text of the debug file.</param>
    /// <param name="map">
    /// The function that returns the text of the line map beside the <c>.s</c> at the path it is
    /// given, or null when there is none.
    /// </param>
    /// <param name="values">
    /// Each value's name as the <c>.s</c> spells it, its <c>type</c> field, its value and its
    /// module's map, or null when the debug file could not be read.
    /// </param>
    /// <param name="problem">What is wrong, or null when the debug file was read.</param>
    /// <returns>True if the debug file was read.</returns>
    internal static bool TryValues(
        string text, Func<string, string?> map,
        [NotNullWhen(true)] out List<(string Name, string? Type, long Value, SourceLines? Lines)>? values,
        [NotNullWhen(false)] out string? problem)
    {
        values = null;
        if (!TryParse(text, out var records, out problem))
            return false;

        // A file record names the modules assembled from it. Each module assembled from a `.s`
        // with a map takes its names from that map.
        var named = new Dictionary<int, SourceLines>();
        foreach (var record in records.Where(record => record.Keyword == "file"))
        {
            if (record["name"] is not { } path || map(path) is not { } beside)
                continue;
            if (!LineMap.TryRead(beside, out var lines, out var wrong))
            {
                problem = $"cannot read {path}{LineMap.Extension}, the line map nt65 wrote for {path}: {wrong}";
                return false;
            }
            foreach (var module in (record["mod"] ?? "").Split('+').Select(Number).OfType<int>())
                named[module] = lines;
        }
        var scopes = new Dictionary<int, int>();
        foreach (var record in records.Where(record => record.Keyword == "scope"))
        {
            if (Number(record["id"]) is { } id && Number(record["mod"]) is { } module)
                scopes[id] = module;
        }

        values = [];
        foreach (var record in records.Where(record => record.Keyword == "sym" && record["type"] != "imp"))
        {
            if (record["name"] is not { } name || Hex(record["val"]) is not { } value)
                continue;
            if (Number(record["scope"]) is not { } scope || !scopes.TryGetValue(scope, out var module))
                continue;
            values.Add((name, record["type"], value, named.GetValueOrDefault(module)));
        }
        return true;
    }

    /// <summary>
    /// Reads the records of a debug file, and checks that it is one ld65 wrote in the version
    /// this class reads.
    /// </summary>
    /// <param name="text">The text of the debug file.</param>
    /// <param name="records">The records, one per line, or null when the text is not a debug file.</param>
    /// <param name="problem">What is wrong, or null when the records were read.</param>
    /// <returns>True if the text is a debug file this class reads.</returns>
    private static bool TryParse(
        string text, [NotNullWhen(true)] out List<Record>? records, [NotNullWhen(false)] out string? problem)
    {
        problem = null;
        records = text.Split('\n').Select(Record.Parse).ToList();
        if (records.Any(record => record.Line.TrimEnd('\r') == Version))
            return true;
        records = null;
        problem = "it is not a version 2.0 ld65 debug file, the kind ld65 writes with `--dbgfile`";
        return false;
    }

    /// <summary>Returns the debug file with the added records in place and the counts updated to match.</summary>
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

    /// <summary>Returns one record as it is written back. Most records are written back unchanged.</summary>
    private static string Rewritten(
        Record record, IReadOnlyDictionary<int, (SourceLines Map, int[] Ids)> sources,
        IReadOnlyDictionary<int, int> replaced, int files, int lines)
    {
        switch (record.Keyword)
        {
            // The counts let a debugger set aside the memory it will need before it reads.
            case "info":
                return record.With(("file", field => Count(field, files)), ("line", field => Count(field, lines)));

            // A module names one file, and the one worth naming is the source it was compiled from.
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

    /// <summary>
    /// Returns a <c>+</c>-separated list of line ids with the ids they were mapped to added.
    /// </summary>
    private static string Also(string field, IReadOnlyDictionary<int, int> replaced)
    {
        var ids = field.Split('+').Select(id => Number(id) ?? -1).ToList();
        var also = ids.Select(id => replaced.GetValueOrDefault(id, -1)).Where(id => id >= 0 && !ids.Contains(id));
        return string.Join('+', ids.Concat(also).Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Returns a count field with <paramref name="more"/> added to it.</summary>
    private static string Count(string field, int more) =>
        ((Number(field) ?? 0) + more).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the <c>span</c> field of a line record, or an empty string when it covers no bytes.
    /// </summary>
    private static string Spanned(IReadOnlyList<string> spans) =>
        spans.Count == 0 ? "" : ",span=" + string.Join('+', spans.Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Returns the first unused id for <paramref name="keyword"/> records, which is past every id
    /// in <paramref name="of"/> and no lower than the count the <c>info</c> record gives.
    /// </summary>
    private static int Next(IReadOnlyList<Record> records, string keyword, IEnumerable<Record> of)
    {
        var counted = Number(records.FirstOrDefault(record => record.Keyword == "info")?[keyword]) ?? 0;
        return of.Aggregate(counted, (next, record) => Math.Max(next, (Number(record["id"]) ?? -1) + 1));
    }

    /// <summary>
    /// Returns the index of the last <paramref name="keyword"/> record, or of the last record when
    /// there is none.
    /// </summary>
    private static int Last(IReadOnlyList<Record> records, string keyword)
    {
        for (var i = records.Count - 1; i >= 0; i--)
        {
            if (records[i].Keyword == keyword)
                return i;
        }
        return records.Count - 1;
    }

    /// <summary>
    /// Returns a field written as ld65 writes a value, such as <c>0x7E2388</c>, as a number, or
    /// null when it is not one.
    /// </summary>
    private static long? Hex(string? field) =>
        field is not null && field.StartsWith("0x", StringComparison.Ordinal)
            && long.TryParse(field[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    /// <summary>Returns a field as a number, or null when it is not a number.</summary>
    private static int? Number(string? field) =>
        field is not null && int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    /// <summary>
    /// Represents one line of a debug file, which is a keyword and a tab followed by
    /// <c>name=value</c> fields separated by commas. A value may be quoted, and so may itself hold
    /// a comma.
    /// </summary>
    private sealed class Record
    {
        private readonly List<(string Name, string Value)> fields = [];

        private Record(string line, string keyword)
        {
            Line = line;
            Keyword = keyword;
        }

        /// <summary>
        /// Gets the line as it arrived, which is what is written back for all but a few records.
        /// </summary>
        public string Line { get; }

        /// <summary>
        /// Gets the keyword that says what the line describes, such as <c>file</c>, <c>line</c> or
        /// <c>sym</c>.
        /// </summary>
        public string Keyword { get; }

        /// <summary>Gets the value of the field named <paramref name="name"/>, unquoted, or null when the record has none.</summary>
        public string? this[string name] =>
            fields.FirstOrDefault(field => field.Name == name) is { Name.Length: > 0 } found
                ? found.Value.Trim('"') : null;

        /// <summary>
        /// Parses one line of a debug file. A line with no tab, such as the version line, is a
        /// record whose keyword is the whole line and which has no fields.
        /// </summary>
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

        /// <summary>Returns the record with <paramref name="changes"/> applied to the fields they name.</summary>
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

        /// <summary>Returns the comma-separated fields of a record, keeping a comma inside quotes.</summary>
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
