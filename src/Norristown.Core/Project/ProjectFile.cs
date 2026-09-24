using System.Globalization;
using System.Text;
using System.Text.Json;
using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Project;

/// <summary>
/// Reads <c>nt65.json</c>. The file is read as data, not executed. An unknown key or a value of
/// the wrong shape is reported, and the rest of the file is still read, so one typo does not
/// hide the next.
/// <para>
/// Diagnostics point at the key they are about. A <see cref="JsonDocument"/> gives values but
/// not where they are written, so one pass of a <see cref="Utf8JsonReader"/> records where each
/// key is. A key that appears twice is a matter for JSON, and nt65 does not check for it. A
/// diagnostic about such a key points at its first appearance.
/// </para>
/// </summary>
public static class ProjectFile
{
    /// <summary>The file name of the project file.</summary>
    public const string Name = "nt65.json";

    private const string CpuKey = "cpu";
    private const string FilesKey = "files";
    private const string OutKey = "out";
    private const string DefinesKey = "defines";
    private const string DiagnosticsKey = "diagnostics";
    private const string SpacesKey = "spaces";
    private const string SegmentsKey = "segments";
    private const string RangesKey = "ranges";
    private const string ConfigurationsKey = "configurations";
    private const string SizeKey = "size";
    private const string MirrorsKey = "mirrors";
    private const string SpaceKey = "space";
    private const string Off = "off";
    private const string Warning = "warning";
    private const string Error = "error";
    private const string Code = "code";
    private const string Data = "data";

    private static readonly string[] known =
        [CpuKey, FilesKey, OutKey, DefinesKey, DiagnosticsKey, SpacesKey, SegmentsKey, RangesKey, ConfigurationsKey];

    /// <summary>
    /// Keys nt65 accepts and reads nothing from. <c>$schema</c> names the schema an editor
    /// validates the file against, which is the editor's business and not the build's.
    /// </summary>
    private static readonly string[] ignored = ["$schema"];

    /// <summary>The options both readers of the file use, which allow comments and trailing commas.</summary>
    private static readonly JsonReaderOptions options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Gets every key the file may hold, for code that describes the file to an editor. The
    /// schema that the editor extension contributes has to be kept in step with these lists.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } = [.. known, .. ignored];

    /// <summary>Gets the keys one named configuration may hold.</summary>
    public static IReadOnlyList<string> ConfigurationKeys { get; } = [DefinesKey, DiagnosticsKey, OutKey];

    /// <summary>
    /// Gets the values a <c>diagnostics</c> entry may give, which are the severity at which to
    /// report that diagnostic, or <c>off</c>.
    /// </summary>
    public static IReadOnlyList<string> Levels { get; } = [Off, Warning, Error];

    /// <summary>Gets the keys one segment may hold.</summary>
    public static IReadOnlyList<string> SegmentKeys { get; } = [SizeKey, "dp", "bank", MirrorsKey, SpaceKey];

    /// <summary>
    /// Gets the values that say what a space may hold, which are code for this program's
    /// processor, or data and macro calls.
    /// </summary>
    public static IReadOnlyList<string> SpaceHolds { get; } = [Code, Data];

    /// <summary>
    /// Reads the project described by <paramref name="text"/>. <paramref name="path"/> is the
    /// logical path that diagnostics use to refer to the file. Problems with the file are
    /// returned in the settings.
    /// </summary>
    public static ProjectSettings Read(string path, string text)
    {
        var diagnostics = new List<Diagnostic>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = options.CommentHandling,
                AllowTrailingCommas = options.AllowTrailingCommas,
            });
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic(
                new Span(path, (int)(exception.LineNumber ?? 0) + 1, (int)(exception.BytePositionInLine ?? 0) + 1,
                    (int)(exception.BytePositionInLine ?? 0) + 2),
                Catalogue.ProjectJsonInvalid.Message(exception.Message.TrimEnd('.').Split(" LineNumber")[0])));
            return ProjectSettings.None with { Diagnostics = diagnostics };
        }

        using (document)
        {
            var root = document.RootElement;
            var reader = new Reader(path, text, diagnostics);
            if (root.ValueKind != JsonValueKind.Object)
            {
                reader.Report(null, Catalogue.ProjectNotAnObject.Message(Name));
                return ProjectSettings.None with { Diagnostics = diagnostics };
            }

            var keys = Key.Scan(text);
            foreach (var property in root.EnumerateObject())
            {
                if (known.Contains(property.Name, StringComparer.Ordinal)
                    || ignored.Contains(property.Name, StringComparer.Ordinal))
                {
                    continue;
                }
                reader.Report(keys[property.Name], Unknown(property.Name));
            }

            return new ProjectSettings(
                reader.Cpu(root, keys),
                reader.Strings(root, keys, FilesKey),
                reader.String(root, keys, OutKey),
                reader.Defines(root, keys),
                reader.Segments(root, keys),
                diagnostics)
            {
                Ranges = reader.Ranges(root, keys),
                Spaces = reader.Spaces(root, keys),
                Severities = reader.Severities(root, keys),
                Configurations = reader.Configurations(root, keys),
            };
        }
    }

    /// <summary>
    /// Reads one <c>-D NAME=value</c> from the command line, or reports what is wrong with it.
    /// <c>-D NAME</c> with no value defines the name as 1, as a flag.
    /// </summary>
    public static Define? Definition(string argument, List<Diagnostic> diagnostics)
    {
        var at = argument.IndexOf('=');
        var name = at < 0 ? argument : argument[..at];
        var span = new Span("-D", 1, 1, argument.Length + 1);
        if (!IsName(name))
        {
            diagnostics.Add(new Diagnostic(span, Catalogue.DefineNameInvalid.Message(name)));
            return null;
        }
        if (at < 0)
            return new Define(name, 1, span);
        if (Number(argument[(at + 1)..]) is not { } value)
        {
            diagnostics.Add(new Diagnostic(span,
                Catalogue.DefineNotANumber.Message(argument[(at + 1)..])));
            return null;
        }
        return new Define(name, value, span);
    }

    /// <summary>
    /// Returns the message for a key nt65 does not know, naming the known key it most nearly
    /// matches, if there is one. A typo is usually a letter away from the key it was meant to be.
    /// </summary>
    private static DiagnosticMessage Unknown(string key)
    {
        var nearest = Spelling.Nearest(key, known);
        return Catalogue.ProjectKeyUnknown.Message(key, Name, nearest is null ? "" : $"; did you mean `{nearest}`?");
    }

    /// <summary>
    /// Parses <paramref name="text"/> as a JSON number or as a number in nt65's number syntax,
    /// or returns null if it is neither.
    /// </summary>
    private static long? Number(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : Literals.Number(text);

    /// <summary>
    /// Reads <paramref name="element"/> as a JSON number or as a string in nt65's number syntax,
    /// or returns null if it is neither.
    /// </summary>
    private static long? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => Number(element.GetRawText()),
        JsonValueKind.String => Number(element.GetString() ?? ""),
        _ => null,
    };

    /// <summary>
    /// Returns a value indicating whether <paramref name="text"/> is a define's name, or a
    /// <c>.config</c>'s name qualified by its module's path, as in <c>hw::SOUND</c>.
    /// </summary>
    private static bool IsName(string text) => text.Split("::").All(part =>
        part.Length > 0 && (char.IsAsciiLetter(part[0]) || part[0] == '_')
        && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));

    /// <summary>
    /// Represents where one key of the file is written, together with the keys of the object that
    /// is its value, if its value is an object. The root stands for the whole file and is written
    /// nowhere.
    /// </summary>
    private sealed class Key(int start, int length)
    {
        private readonly Dictionary<string, Key> keys = new(StringComparer.Ordinal);

        /// <summary>Gets the offset in the text of the key's opening quote.</summary>
        public int Start => start;

        /// <summary>Gets the length of the key as written, including its quotes and any escapes.</summary>
        public int Length => length;

        /// <summary>
        /// Gets the first key named <paramref name="name"/> in this key's object, or null when
        /// the object has no such key or the value is not an object.
        /// </summary>
        public Key? this[string name] => keys.GetValueOrDefault(name);

        /// <summary>
        /// Returns the keys of <paramref name="text"/>, found in one pass of a JSON reader. Keys
        /// inside an array are not recorded, since no diagnostic points at one.
        /// </summary>
        public static Key Scan(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var root = new Key(-1, 0);
            var open = new Stack<Key?>();
            Key? value = null;
            var (bytesSeen, charsSeen) = (0, 0);
            int Offset(int at)
            {
                charsSeen += Encoding.UTF8.GetCharCount(bytes, bytesSeen, at - bytesSeen);
                bytesSeen = at;
                return charsSeen;
            }

            var reader = new Utf8JsonReader(bytes, options);
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        value = null;
                        if (open.Peek() is not { } parent)
                            break;
                        var name = reader.GetString() ?? "";
                        var start = (int)reader.TokenStartIndex;
                        var from = Offset(start);
                        var key = new Key(from, Offset(start + reader.ValueSpan.Length + 2) - from);
                        value = parent.keys.TryAdd(name, key) ? key : parent.keys[name];
                        break;
                    case JsonTokenType.StartObject:
                        open.Push(open.Count == 0 ? root : value);
                        value = null;
                        break;
                    case JsonTokenType.StartArray:
                        open.Push(null);
                        value = null;
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        open.Pop();
                        break;
                    default:
                        value = null;
                        break;
                }
            }
            return root;
        }
    }

    /// <summary>Reads one project file, keeping its text so that diagnostics can point into it.</summary>
    private sealed class Reader(string path, string text, List<Diagnostic> diagnostics)
    {
        public Cpu? Cpu(JsonElement root, Key keys)
        {
            if (String(root, keys, CpuKey) is not { } named)
                return null;
            if (CpuNames.Parse(named) is { } cpu)
                return cpu;
            Report(keys[CpuKey], Catalogue.ProjectCpuUnknown.Message(named, CpuNames.Listed));
            return null;
        }

        /// <summary>
        /// Reads the <c>defines</c> of <paramref name="owner"/>, which is the project or one of
        /// its configurations. <paramref name="keys"/> is where the owner's keys are written, so
        /// that a define both of them give is reported in the object being read.
        /// </summary>
        public IReadOnlyList<Define> Defines(JsonElement owner, Key? keys)
        {
            if (!Object(owner, keys, DefinesKey, out var defines))
                return [];

            var within = keys?[DefinesKey];
            var read = new List<Define>();
            foreach (var property in defines.EnumerateObject())
            {
                var key = within?[property.Name];
                if (!IsName(property.Name))
                {
                    Report(key, Catalogue.DefineNameInvalid.Message(property.Name));
                    continue;
                }
                if (Number(property.Value) is not { } value)
                {
                    Report(key, Catalogue.DefineNotANumber.Message(property.Name));
                    continue;
                }
                read.Add(new Define(property.Name, value, At(key)));
            }
            return [.. read.OrderBy(define => define.Name, StringComparer.Ordinal)];
        }

        /// <summary>
        /// Reads a <c>diagnostics</c> object, such as
        /// <c>"diagnostics": { "unused-symbol": "off" }</c>, which gives the severity at which the
        /// project reports each named diagnostic, overriding the one the catalogue gives it.
        /// <paramref name="keys"/> is where the keys of the object being read are written, so a
        /// configuration's entry is reported where that configuration gives it.
        /// </summary>
        public IReadOnlyDictionary<string, Severity?> Severities(JsonElement owner, Key? keys)
        {
            if (!Object(owner, keys, DiagnosticsKey, out var section))
                return ProjectSettings.NoSeverities;

            var read = new SortedDictionary<string, Severity?>(StringComparer.Ordinal);
            var within = keys?[DiagnosticsKey];
            foreach (var property in section.EnumerateObject())
            {
                var key = within?[property.Name];
                if (Catalogue.Find(property.Name) is not { } descriptor)
                {
                    var nearest = Spelling.Nearest(property.Name, Catalogue.All.Select(d => d.Id));
                    Report(
                        key,
                        Catalogue.DiagnosticNameUnknown.Message(
                            property.Name, nearest is null ? "" : $"; did you mean `{nearest}`?"));
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.String
                    || Level(property.Value.GetString(), out var level) is false)
                {
                    Report(key, Catalogue.DiagnosticSeverityUnknown.Message(property.Name));
                    continue;
                }
                if (descriptor.Severity == Severity.Error && level != Severity.Error)
                {
                    Report(key, Catalogue.DiagnosticNotTurnedDown.Message(property.Name));
                    continue;
                }
                read[property.Name] = level;
            }
            return read;
        }

        /// <summary>
        /// Reads the named configurations, each of which gives defines over the project's and an
        /// output directory, as in
        /// <c>"debug": { "defines": { "DEBUG": 1 }, "out": "build/debug" }</c>.
        /// </summary>
        public IReadOnlyList<BuildConfiguration> Configurations(JsonElement root, Key keys)
        {
            if (!Object(root, keys, ConfigurationsKey, out var configurations))
                return [];

            var read = new List<BuildConfiguration>();
            var within = keys[ConfigurationsKey];
            foreach (var property in configurations.EnumerateObject())
            {
                var key = within?[property.Name];
                if (property.Name.Length == 0 || !property.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                {
                    Report(key, Catalogue.ConfigurationNameInvalid.Message(property.Name));
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    Report(key, Catalogue.ConfigurationNotAnObject.Message(property.Name));
                    continue;
                }
                foreach (var setting in property.Value.EnumerateObject())
                {
                    if (!ConfigurationKeys.Contains(setting.Name, StringComparer.Ordinal))
                        Report(key?[setting.Name], Catalogue.ConfigurationKeyUnknown.Message(property.Name, setting.Name));
                }
                read.Add(new BuildConfiguration(
                    property.Name,
                    Defines(property.Value, key),
                    String(property.Value, key, OutKey),
                    At(key))
                {
                    Severities = Severities(property.Value, key),
                });
            }
            return [.. read.OrderBy(configuration => configuration.Name, StringComparer.Ordinal)];
        }

        public IReadOnlyList<Segment> Segments(JsonElement root, Key keys)
        {
            if (!Object(root, keys, SegmentsKey, out var segments))
                return [];

            var read = new List<Segment>();
            var within = keys[SegmentsKey];
            foreach (var property in segments.EnumerateObject())
            {
                var key = within?[property.Name];
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    Report(key, Catalogue.ProjectSegmentNotAnObject.Message(property.Name));
                    continue;
                }

                var size = property.Value.TryGetProperty(SizeKey, out var sizeElement) && sizeElement.ValueKind == JsonValueKind.String
                    ? SegmentNames.ParseSize(sizeElement.GetString() ?? "")
                    : null;
                if (size is not { } address)
                {
                    Report(key, Catalogue.ProjectSegmentSizeMissing.Message(property.Name));
                    continue;
                }
                var segment = new Segment(property.Name, address, At(key));
                foreach (var attribute in property.Value.EnumerateObject())
                {
                    if (!SegmentKeys.Contains(attribute.Name, StringComparer.Ordinal))
                    {
                        Report(key, Catalogue.ProjectSegmentKeyUnknown.Message(property.Name, attribute.Name));
                        continue;
                    }
                    if (attribute.Name == SizeKey)
                        continue;
                    if (attribute.Name == SpaceKey)
                    {
                        if (attribute.Value.ValueKind == JsonValueKind.String && attribute.Value.GetString() is { Length: > 0 } space)
                            segment = segment with { Space = space };
                        else
                            Report(key, Catalogue.SpaceNotAName);
                        continue;
                    }
                    if (attribute.Name == MirrorsKey)
                    {
                        segment = segment with { Mirrors = Banks(property.Name, key, attribute.Value) };
                        continue;
                    }
                    if (StateRegister.FromAttribute(attribute.Name) is not { } register)
                        continue;
                    var value = Number(attribute.Value);
                    if (Semantics.SegmentTable.Check(property.Name, address, register, value, At(key),
                        (segment.DirectPage, segment.Bank), diagnostics) is not { } valid)
                    {
                        continue;
                    }
                    segment = register == StateRegister.DirectPage ? segment with { DirectPage = valid } : segment with { Bank = valid };
                }
                if (segment is { Mirrors.Count: > 0, Bank: null })
                    Report(key, SegmentTable.MirrorsNeedABank(property.Name));
                read.Add(segment);
            }
            return [.. read.OrderBy(segment => segment.Name, StringComparer.Ordinal)];
        }

        /// <summary>
        /// Reads the address spaces other than the host's, each with whether it runs this
        /// program's processor, as in <c>"spc": "data"</c>.
        /// </summary>
        public IReadOnlyList<AddressSpace> Spaces(JsonElement root, Key keys)
        {
            if (!Object(root, keys, SpacesKey, out var spaces))
                return [];
            var read = new List<AddressSpace>();
            var within = keys[SpacesKey];
            foreach (var property in spaces.EnumerateObject())
            {
                var key = within?[property.Name];
                var holds = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                if (holds is null || !SpaceHolds.Contains(holds, StringComparer.Ordinal))
                {
                    Report(key, Catalogue.ProjectSpaceHoldsUnknown.Message(property.Name));
                    continue;
                }
                read.Add(new AddressSpace(property.Name, holds == Code, At(key)));
            }
            return [.. read.OrderBy(space => space.Name, StringComparer.Ordinal)];
        }

        /// <summary>
        /// Reads the ranges, each giving the banks from which an absolute constant address in it
        /// may be reached, as in <c>"$2100-$21ff": ["$00-$3f", "$80-$bf"]</c>. A range is given as
        /// <c>first-last</c> or as one address. Two ranges may not overlap, so each address has
        /// only one answer.
        /// </summary>
        public IReadOnlyList<AccessRange> Ranges(JsonElement root, Key keys)
        {
            if (!Object(root, keys, RangesKey, out var ranges))
                return [];

            var read = new List<AccessRange>();
            var within = keys[RangesKey];
            foreach (var property in ranges.EnumerateObject())
            {
                var key = within?[property.Name];
                if (Interval(property.Name, 0xffff) is not { } addresses)
                {
                    Report(key, Catalogue.RangeInvalid.Message(property.Name));
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    Report(key, Catalogue.BanksNotAList.Message(property.Name));
                    continue;
                }
                var range = new AccessRange(addresses.First, addresses.Last, Banks(property.Name, key, property.Value));
                if (read.FirstOrDefault(other => other.First <= range.Last && range.First <= other.Last) is { } overlapping)
                {
                    Report(key, Catalogue.RangesOverlap.Message(
                        property.Name, StateValue.Hex(overlapping.First, 4), StateValue.Hex(overlapping.Last, 4)));
                    continue;
                }
                read.Add(range);
            }
            return [.. read.OrderBy(range => range.First)];
        }

        /// <summary>
        /// Reads a list of banks, each one bank or a range of them, as in
        /// <c>["$00-$3f", "$80-$bf"]</c>. Problems are reported at <paramref name="key"/>, the
        /// key named <paramref name="name"/> that the list belongs to.
        /// </summary>
        public List<(long First, long Last)> Banks(string name, Key? key, JsonElement value)
        {
            var banks = new List<(long First, long Last)>();
            if (value.ValueKind != JsonValueKind.Array)
            {
                Report(key, Catalogue.BanksNotAList.Message(name));
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
                    Report(key, Catalogue.BankInvalid.Message(name, item.GetRawText()));
            }
            return banks;
        }

        public IReadOnlyList<string> Strings(JsonElement owner, Key? keys, string name)
        {
            if (!owner.TryGetProperty(name, out var value))
                return [];
            if (value.ValueKind != JsonValueKind.Array)
            {
                Report(keys?[name], Catalogue.ProjectNotAList.Message(name));
                return [];
            }

            var read = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    read.Add(item.GetString() ?? "");
                else
                    Report(keys?[name], Catalogue.ProjectNotAList.Message(name));
            }
            return read;
        }

        public string? String(JsonElement owner, Key? keys, string name)
        {
            if (!owner.TryGetProperty(name, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            Report(keys?[name], Catalogue.ProjectNotAString.Message(name));
            return null;
        }

        /// <summary>
        /// Reports <paramref name="message"/> at <paramref name="key"/>, or at the start of the
        /// file when there is no key to point at.
        /// </summary>
        public void Report(Key? key, DiagnosticMessage message) =>
            diagnostics.Add(new Diagnostic(At(key), Severity.Error, message));

        /// <summary>
        /// Returns a value indicating whether <paramref name="word"/> is one of the three
        /// severity words, and sets <paramref name="level"/> to the severity it names. The word
        /// <c>off</c> gives null.
        /// </summary>
        private static bool Level(string? word, out Severity? level)
        {
            level = word switch
            {
                Warning => Severity.Warning,
                Error => Severity.Error,
                _ => null,
            };
            return word is not null && Levels.Contains(word, StringComparer.Ordinal);
        }

        /// <summary>
        /// Parses <paramref name="text"/> as <c>first-last</c> or as a single number, each no
        /// greater than <paramref name="largest"/>, or returns null if it is neither.
        /// </summary>
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

        /// <summary>
        /// Returns a value indicating whether <paramref name="owner"/> has an object under
        /// <paramref name="name"/>, and reports the key if its value is something else.
        /// </summary>
        private bool Object(JsonElement owner, Key? keys, string name, out JsonElement value)
        {
            if (!owner.TryGetProperty(name, out value))
                return false;
            if (value.ValueKind == JsonValueKind.Object)
                return true;
            Report(keys?[name], Catalogue.ProjectValueNotAnObject.Message(name));
            return false;
        }

        /// <summary>
        /// Returns the span where <paramref name="key"/> is written, or the first character of the
        /// file when there is no key.
        /// </summary>
        private Span At(Key? key)
        {
            if (key is null || key.Start < 0)
                return new Span(path, 1, 1, 2);
            var line = 1;
            var start = 0;
            for (var i = 0; i < key.Start; i++)
            {
                if (text[i] != '\n')
                    continue;
                line++;
                start = i + 1;
            }
            return new Span(path, line, key.Start - start + 1, key.Start - start + key.Length + 1);
        }
    }
}
