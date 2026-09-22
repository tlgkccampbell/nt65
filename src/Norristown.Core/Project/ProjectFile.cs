using System.Globalization;
using System.Text.Json;
using Norristown.Processor;
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

    private static readonly string[] known =
        ["cpu", "files", "out", "defines", "diagnostics", "segments", "ranges", "configurations"];

    /// <summary>
    /// Keys nt65 accepts and reads nothing from. <c>$schema</c> names the schema an editor
    /// validates the file against, which is the editor's business and not the build's.
    /// </summary>
    private static readonly string[] ignored = ["$schema"];

    /// <summary>
    /// Every key the file may hold, for whoever describes it to an editor. The schema the
    /// extension contributes is kept in step with these three lists.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } = [.. known, .. ignored];

    /// <summary>The keys one named configuration may hold.</summary>
    public static IReadOnlyList<string> ConfigurationKeys { get; } = ["defines", "diagnostics", "out"];

    /// <summary>What a <c>diagnostics</c> entry may say, which is how much its name matters.</summary>
    public static IReadOnlyList<string> Levels { get; } = ["off", "warning", "error"];

    /// <summary>The keys one segment may hold.</summary>
    public static IReadOnlyList<string> SegmentKeys { get; } = ["size", "dp", "bank", "mirrors"];

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
                Catalogue.ProjectJsonInvalid.Says(exception.Message.TrimEnd('.').Split(" LineNumber")[0])));
            return ProjectSettings.None with { Diagnostics = diagnostics };
        }

        using (document)
        {
            var reader = new Reader(path, text, diagnostics);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reader.Report("", Catalogue.ProjectNotAnObject.Says(Name));
                return ProjectSettings.None with { Diagnostics = diagnostics };
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (known.Contains(property.Name, StringComparer.Ordinal)
                    || ignored.Contains(property.Name, StringComparer.Ordinal))
                {
                    continue;
                }
                reader.Report(property.Name, Unknown(property.Name));
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
                Severities = reader.Severities(document.RootElement),
                Configurations = reader.Configurations(document.RootElement),
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
            diagnostics.Add(new Diagnostic(span, Catalogue.DefineNameInvalid.Says(name)));
            return null;
        }
        if (at < 0)
            return new Define(name, 1, span);
        if (Number(argument[(at + 1)..]) is not { } value)
        {
            diagnostics.Add(new Diagnostic(span,
                Catalogue.DefineNotANumber.Says(argument[(at + 1)..])));
            return null;
        }
        return new Define(name, value, span);
    }

    /// <summary>
    /// What a key nt65 does not know is reported as, with the key it is nearly when there is
    /// one: a typo is one letter from the key it was meant to be.
    /// </summary>
    private static DiagnosticMessage Unknown(string key)
    {
        var nearest = Spelling.Nearest(key, known);
        return Catalogue.ProjectKeyUnknown.Says(key, Name, nearest is null ? "" : $"; `{nearest}` is");
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

    /// <summary>A define's name, or a <c>.config</c>'s written with its module's path, as <c>hw::SOUND</c>.</summary>
    private static bool IsName(string text) => text.Split("::").All(part =>
        part.Length > 0 && (char.IsAsciiLetter(part[0]) || part[0] == '_')
        && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));

    /// <summary>One project file being read, with the text kept so diagnostics can point into it.</summary>
    private sealed class Reader(string path, string text, List<Diagnostic> diagnostics)
    {
        public Cpu? Cpu(JsonElement root)
        {
            if (String(root, "cpu") is not { } named)
                return null;
            if (CpuNames.Parse(named) is { } cpu)
                return cpu;
            Report("cpu", Catalogue.ProjectCpuUnknown.Says(named, CpuNames.Listed));
            return null;
        }

        /// <summary>
        /// The <c>defines</c> of <paramref name="root"/>, which is the project or one of its
        /// configurations; <paramref name="from"/> is where that is written, so a define both
        /// give is reported where this one gives it.
        /// </summary>
        public IReadOnlyList<Define> Defines(JsonElement root, int from = 0)
        {
            if (!Object(root, "defines", out var defines, from))
                return [];

            var read = new List<Define>();
            foreach (var property in defines.EnumerateObject())
            {
                if (!IsName(property.Name))
                {
                    Report(property.Name, Catalogue.DefineNameInvalid.Says(property.Name), from);
                    continue;
                }
                if (Number(property.Value) is not { } value)
                {
                    Report(property.Name, Catalogue.DefineNotANumber.Says(property.Name), from);
                    continue;
                }
                read.Add(new Define(property.Name, value, At(property.Name, from)));
            }
            return [.. read.OrderBy(define => define.Name, StringComparer.Ordinal)];
        }

        /// <summary>
        /// <c>"diagnostics": { "unused-symbol": "off" }</c>: how much each named diagnostic
        /// matters to this project, over the severity the catalogue gives it.
        /// <paramref name="from"/> is where the object being read is written, so a configuration's
        /// entry is reported where that configuration gives it.
        /// </summary>
        public IReadOnlyDictionary<string, Severity?> Severities(JsonElement root, int from = 0)
        {
            if (!Object(root, "diagnostics", out var said, from))
                return ProjectSettings.NoSeverities;

            var read = new SortedDictionary<string, Severity?>(StringComparer.Ordinal);
            var within = Offset("diagnostics", from);
            foreach (var property in said.EnumerateObject())
            {
                if (Catalogue.Find(property.Name) is not { } descriptor)
                {
                    var nearest = Spelling.Nearest(property.Name, Catalogue.All.Select(d => d.Id));
                    Report(
                        property.Name,
                        Catalogue.DiagnosticNameUnknown.Says(
                            property.Name, nearest is null ? "" : $"; `{nearest}` is"),
                        within);
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.String
                    || Level(property.Value.GetString(), out var level) is false)
                {
                    Report(property.Name, Catalogue.DiagnosticSeverityUnknown.Says(property.Name), within);
                    continue;
                }
                if (descriptor.Severity == Severity.Error && level != Severity.Error)
                {
                    Report(property.Name, Catalogue.DiagnosticNotTurnedDown.Says(property.Name), within);
                    continue;
                }
                read[property.Name] = level;
            }
            return read;
        }

        /// <summary>
        /// <c>"debug": { "defines": { "DEBUG": 1 }, "out": "build/debug" }</c>: the named
        /// configurations, each giving defines over the project's and an output directory.
        /// </summary>
        public IReadOnlyList<BuildConfiguration> Configurations(JsonElement root)
        {
            if (!Object(root, "configurations", out var configurations))
                return [];

            var read = new List<BuildConfiguration>();
            var within = Offset("configurations");
            foreach (var property in configurations.EnumerateObject())
            {
                var from = Offset(property.Name, within);
                if (property.Name.Length == 0 || !property.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                {
                    Report(property.Name, Catalogue.ConfigurationNameInvalid.Says(property.Name), within);
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    Report(property.Name, Catalogue.ConfigurationNotAnObject.Says(property.Name), within);
                    continue;
                }
                foreach (var key in property.Value.EnumerateObject())
                {
                    if (!ConfigurationKeys.Contains(key.Name, StringComparer.Ordinal))
                    {
                        Report(key.Name, Catalogue.ConfigurationKeyUnknown.Says(property.Name, key.Name), from);
                    }
                }
                read.Add(new BuildConfiguration(
                    property.Name,
                    Defines(property.Value, from),
                    String(property.Value, "out", from),
                    At(property.Name, within))
                {
                    Severities = Severities(property.Value, from),
                });
            }
            return [.. read.OrderBy(configuration => configuration.Name, StringComparer.Ordinal)];
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
                    Report(property.Name, Catalogue.ProjectSegmentNotAnObject.Says(property.Name));
                    continue;
                }

                var size = property.Value.TryGetProperty("size", out var written) && written.ValueKind == JsonValueKind.String
                    ? SegmentNames.ParseSize(written.GetString() ?? "")
                    : null;
                if (size is not { } address)
                {
                    Report(property.Name, Catalogue.ProjectSegmentSizeMissing.Says(property.Name));
                    continue;
                }
                var segment = new Segment(property.Name, address, At(property.Name));
                foreach (var attribute in property.Value.EnumerateObject())
                {
                    if (!SegmentKeys.Contains(attribute.Name, StringComparer.Ordinal))
                    {
                        Report(property.Name, Catalogue.ProjectSegmentKeyUnknown.Says(property.Name, attribute.Name));
                        continue;
                    }
                    if (attribute.Name == "size")
                        continue;
                    if (attribute.Name == "mirrors")
                    {
                        segment = segment with { Mirrors = Banks(property.Name, attribute.Value) };
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
                if (segment is { Mirrors.Count: > 0, Bank: null })
                    Report(property.Name, SegmentTable.MirrorsNeedABank(property.Name));
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
                    Report(property.Name, Catalogue.RangeInvalid.Says(property.Name));
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    Report(property.Name, Catalogue.BanksNotAList.Says(property.Name));
                    continue;
                }
                var range = new AccessRange(addresses.First, addresses.Last, Banks(property.Name, property.Value));
                if (read.FirstOrDefault(other => other.First <= range.Last && range.First <= other.Last) is { } overlapping)
                {
                    Report(property.Name, Catalogue.RangesOverlap.Says(
                        property.Name, StateValue.Hex(overlapping.First, 4), StateValue.Hex(overlapping.Last, 4)));
                    continue;
                }
                read.Add(range);
            }
            return [.. read.OrderBy(range => range.First)];
        }

        /// <summary><c>["$00-$3f", "$80-$bf"]</c>: banks, each one bank or a range of them.</summary>
        public List<(long First, long Last)> Banks(string key, JsonElement value)
        {
            var banks = new List<(long First, long Last)>();
            if (value.ValueKind != JsonValueKind.Array)
            {
                Report(key, Catalogue.BanksNotAList.Says(key));
                return banks;
            }
            foreach (var item in value.EnumerateArray())
            {
                var bank = item.ValueKind == JsonValueKind.String ? Interval(item.GetString() ?? "", 0xff)
                    : Number(item) is { } one and >= 0 and <= 0xff ? (one, one)
                    : null;
                if (bank is { } valid)
                    banks.Add(valid);
                else
                    Report(key, Catalogue.BankInvalid.Says(key, item.GetRawText()));
            }
            return banks;
        }

        public IReadOnlyList<string> Strings(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var value))
                return [];
            if (value.ValueKind != JsonValueKind.Array)
            {
                Report(key, Catalogue.ProjectNotAList.Says(key));
                return [];
            }

            var read = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    read.Add(item.GetString() ?? "");
                else
                    Report(key, Catalogue.ProjectNotAList.Says(key));
            }
            return read;
        }

        public string? String(JsonElement root, string key, int from = 0)
        {
            if (!root.TryGetProperty(key, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            Report(key, Catalogue.ProjectNotAString.Says(key), from);
            return null;
        }

        public void Report(string key, DiagnosticMessage message, int from = 0) =>
            diagnostics.Add(new Diagnostic(At(key, from), Severity.Error, message));

        /// <summary>How much a diagnostic matters, where the word is one of the three; <c>off</c> is no severity.</summary>
        private static bool Level(string? written, out Severity? level)
        {
            level = written switch
            {
                "warning" => Severity.Warning,
                "error" => Severity.Error,
                _ => null,
            };
            return written is "off" or "warning" or "error";
        }

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

        private bool Object(JsonElement root, string key, out JsonElement value, int from = 0)
        {
            if (!root.TryGetProperty(key, out value))
                return false;
            if (value.ValueKind == JsonValueKind.Object)
                return true;
            Report(key, Catalogue.ProjectValueNotAnObject.Says(key), from);
            return false;
        }

        /// <summary>Where a key is first written at or after <paramref name="from"/>, or <paramref name="from"/> when it is not.</summary>
        private int Offset(string key, int from = 0) =>
            text.IndexOf($"\"{key}\"", from, StringComparison.Ordinal) is var at && at >= 0 ? at : from;

        /// <summary>
        /// Where a key is written, at or after <paramref name="from"/>. The text is searched for
        /// it rather than tracked while parsing, which is enough to put a diagnostic on the right line.
        /// </summary>
        private Span At(string key, int from = 0)
        {
            var at = key.Length == 0 ? -1 : text.IndexOf($"\"{key}\"", from, StringComparison.Ordinal);
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
