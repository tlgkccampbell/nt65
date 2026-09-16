using System.Globalization;
using System.Text.Json;
using Norristown.Semantics;

namespace Norristown.Project;

/// <summary>
/// Reads <c>nt65.json</c>. The file is read as data, not executed: an unknown key or
/// a value of the wrong shape is reported and the rest of the file is still read, so one
/// typo does not hide the next.
/// <para>
/// Diagnostics point at the key they are about, found by searching the text for it. That is
/// enough for an editor to put a squiggle in the right place without a JSON parser that
/// tracks positions, and a key written twice is a JSON question rather than an nt65 one.
/// </para>
/// </summary>
public static class ProjectFile
{
    /// <summary>What the project file is called.</summary>
    public const string Name = "nt65.json";

    private static readonly string[] known = ["cpu", "files", "out", "defines", "segments", "ranges"];

    /// <summary>
    /// Reads the project described by <paramref name="text"/>. <paramref name="path"/> is the
    /// logical path diagnostics name it by; what is wrong with it comes back in the settings.
    /// </summary>
    public static ProjectSettings Read(string path, string text)
    {
        var diagnostics = new List<Diagnostic>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic(
                new Span(path, (int)(exception.LineNumber ?? 0) + 1, (int)(exception.BytePositionInLine ?? 0) + 1,
                    (int)(exception.BytePositionInLine ?? 0) + 2),
                Severity.Error,
                exception.Message.TrimEnd('.').Split(" LineNumber")[0]));
            return ProjectSettings.None with { Diagnostics = diagnostics };
        }

        using (document)
        {
            var reader = new Reader(path, text, diagnostics);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reader.Report("", $"{Name} holds one object");
                return ProjectSettings.None with { Diagnostics = diagnostics };
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!known.Contains(property.Name, StringComparer.Ordinal))
                    reader.Report(property.Name, $"`{property.Name}` is not a {Name} key");
            }

            return new ProjectSettings(
                reader.Cpu(document.RootElement),
                reader.Strings(document.RootElement, "files"),
                reader.String(document.RootElement, "out"),
                reader.Defines(document.RootElement),
                reader.Segments(document.RootElement),
                diagnostics)
            {
                Ranges = reader.Ranges(document.RootElement),
            };
        }
    }

    /// <summary>
    /// Reads one <c>-D NAME=value</c> from the command line, or reports what is wrong
    /// with it. <c>-D NAME</c> with no value defines it as 1, as a flag.
    /// </summary>
    public static Define? Definition(string argument, List<Diagnostic> diagnostics)
    {
        var at = argument.IndexOf('=');
        var name = at < 0 ? argument : argument[..at];
        var span = new Span("-D", 1, 1, argument.Length + 1);
        if (!IsName(name))
        {
            diagnostics.Add(new Diagnostic(span, Severity.Error, $"`{name}` is not a name"));
            return null;
        }
        if (at < 0)
            return new Define(name, 1, span);
        if (Number(argument[(at + 1)..]) is not { } value)
        {
            diagnostics.Add(new Diagnostic(span, Severity.Error,
                $"`{argument[(at + 1)..]}` is not a number, and a define is a number"));
            return null;
        }
        return new Define(name, value, span);
    }

    /// <summary>A JSON number, or a string in nt65's number syntax.</summary>
    private static long? Number(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : Literals.Number(text);

    /// <summary>A JSON number, or a string in nt65's number syntax, as a JSON value.</summary>
    private static long? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => Number(element.GetRawText()),
        JsonValueKind.String => Number(element.GetString() ?? ""),
        _ => null,
    };

    private static bool IsName(string text) =>
        text.Length > 0 && (char.IsAsciiLetter(text[0]) || text[0] == '_')
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>One project file being read, with the text kept so diagnostics can point into it.</summary>
    private sealed class Reader(string path, string text, List<Diagnostic> diagnostics)
    {
        public Cpu? Cpu(JsonElement root)
        {
            if (String(root, "cpu") is not { } named)
                return null;
            if (CpuNames.Parse(named) is { } cpu)
                return cpu;
            Report("cpu", $"`{named}` is not a processor nt65 knows: 6502, 65c02 or 65816");
            return null;
        }

        public IReadOnlyList<Define> Defines(JsonElement root)
        {
            if (!Object(root, "defines", out var defines))
                return [];

            var read = new List<Define>();
            foreach (var property in defines.EnumerateObject())
            {
                if (!IsName(property.Name))
                {
                    Report(property.Name, $"`{property.Name}` is not a name");
                    continue;
                }
                if (Number(property.Value) is not { } value)
                {
                    Report(property.Name, $"`{property.Name}` is not a number, and a define is a number");
                    continue;
                }
                read.Add(new Define(property.Name, value, At(property.Name)));
            }
            return [.. read.OrderBy(define => define.Name, StringComparer.Ordinal)];
        }

        public IReadOnlyList<Segment> Segments(JsonElement root)
        {
            if (!Object(root, "segments", out var segments))
                return [];

            var read = new List<Segment>();
            foreach (var property in segments.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    Report(property.Name, $"segment \"{property.Name}\" is an object with a `size`");
                    continue;
                }

                var size = property.Value.TryGetProperty("size", out var written) && written.ValueKind == JsonValueKind.String
                    ? SegmentNames.ParseSize(written.GetString() ?? "")
                    : null;
                if (size is not { } address)
                {
                    Report(property.Name, $"segment \"{property.Name}\" needs a `size` of \"zp\", \"abs\" or \"far\"");
                    continue;
                }
                var segment = new Segment(property.Name, address, At(property.Name));
                foreach (var attribute in property.Value.EnumerateObject())
                {
                    if (attribute.Name == "size")
                        continue;
                    if (attribute.Name is not ("dp" or "bank"))
                    {
                        Report(property.Name, $"segment \"{property.Name}\": `{attribute.Name}` is not a segment key: "
                            + "a segment has a `size`, a `dp` and a `bank`");
                        continue;
                    }
                    var value = Number(attribute.Value);
                    if (Semantics.SegmentTable.Check(property.Name, address, attribute.Name, value, At(property.Name),
                        (segment.DirectPage, segment.Bank), diagnostics) is not { } valid)
                    {
                        continue;
                    }
                    segment = attribute.Name == "dp" ? segment with { DirectPage = valid } : segment with { Bank = valid };
                }
                read.Add(segment);
            }
            return [.. read.OrderBy(segment => segment.Name, StringComparer.Ordinal)];
        }

        /// <summary>
        /// <c>"$2100-$21ff": ["$00-$3f", "$80-$bf"]</c>: the banks an absolute constant address
        /// in each range may be reached from. A range is written <c>first-last</c> or as one
        /// address, and two ranges may not overlap, so an address has one answer.
        /// </summary>
        public IReadOnlyList<AccessRange> Ranges(JsonElement root)
        {
            if (!Object(root, "ranges", out var ranges))
                return [];

            var read = new List<AccessRange>();
            foreach (var property in ranges.EnumerateObject())
            {
                if (Interval(property.Name, 0xffff) is not { } addresses)
                {
                    Report(property.Name, $"`{property.Name}` is not a range of absolute addresses, such as \"$2100-$21ff\"");
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    Report(property.Name, $"`{property.Name}` is a list of banks, such as [\"$00-$3f\", \"$80-$bf\"]");
                    continue;
                }
                var banks = new List<(long First, long Last)>();
                foreach (var item in property.Value.EnumerateArray())
                {
                    var bank = item.ValueKind == JsonValueKind.String ? Interval(item.GetString() ?? "", 0xff)
                        : Number(item) is { } one and >= 0 and <= 0xff ? (one, one)
                        : null;
                    if (bank is { } valid)
                        banks.Add(valid);
                    else
                        Report(property.Name, $"`{property.Name}`: {item.GetRawText()} is not a bank or a range of banks");
                }
                var range = new AccessRange(addresses.First, addresses.Last, banks);
                if (read.FirstOrDefault(other => other.First <= range.Last && range.First <= other.Last) is { } overlapping)
                {
                    Report(property.Name, $"`{property.Name}` overlaps `{StateValue.Hex(overlapping.First, 4)}-"
                        + $"{StateValue.Hex(overlapping.Last, 4)}`: an address is in one range at most");
                    continue;
                }
                read.Add(range);
            }
            return [.. read.OrderBy(range => range.First)];
        }

        public IReadOnlyList<string> Strings(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var value))
                return [];
            if (value.ValueKind != JsonValueKind.Array)
            {
                Report(key, $"`{key}` is a list of strings");
                return [];
            }

            var read = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    read.Add(item.GetString() ?? "");
                else
                    Report(key, $"`{key}` is a list of strings");
            }
            return read;
        }

        public string? String(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            Report(key, $"`{key}` is a string");
            return null;
        }

        public void Report(string key, string message) =>
            diagnostics.Add(new Diagnostic(At(key), Severity.Error, message));

        /// <summary><c>first-last</c> or a single number, each no more than <paramref name="largest"/>.</summary>
        private static (long First, long Last)? Interval(string text, long largest)
        {
            var parts = text.Split('-');
            if (parts.Length > 2 || Number(parts[0].Trim()) is not { } first
                || Number(parts[^1].Trim()) is not { } last)
            {
                return null;
            }
            return first >= 0 && first <= last && last <= largest ? (first, last) : null;
        }

        private bool Object(JsonElement root, string key, out JsonElement value)
        {
            if (!root.TryGetProperty(key, out value))
                return false;
            if (value.ValueKind == JsonValueKind.Object)
                return true;
            Report(key, $"`{key}` is an object");
            return false;
        }

        /// <summary>
        /// Where a key is written. The text is searched for it rather than tracked while
        /// parsing, which is enough to put a diagnostic on the right line.
        /// </summary>
        private Span At(string key)
        {
            var at = key.Length == 0 ? -1 : text.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            if (at < 0)
                return new Span(path, 1, 1, 2);
            var line = 1;
            var start = 0;
            for (var i = 0; i < at; i++)
            {
                if (text[i] != '\n')
                    continue;
                line++;
                start = i + 1;
            }
            return new Span(path, line, at - start + 1, at - start + key.Length + 3);
        }
    }
}
