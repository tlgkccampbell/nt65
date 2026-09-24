using System.Text.Json;
using System.Text.RegularExpressions;
using Norristown.Cli;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Tests.Editors;

/// <summary>
/// Tests what the VS Code extension contributes against what nt65 itself does. The schema for
/// the project file is checked against the reader, and the problem matcher against the lines
/// <c>nt65 build</c> writes. Both are data files that nothing compiles, so nothing else would
/// notice them drifting.
/// </summary>
public sealed class ExtensionTests : IDisposable
{
    /// <summary>The client's own code, which the manifest has to agree with.</summary>
    private static readonly string[] ClientFiles = ["extension.js", "views.js"];

    private static readonly JsonDocument Package = Read("package.json");
    private static readonly JsonDocument Schema = Read("nt65.schema.json");
    private static readonly JsonDocument Language = Read("language-configuration.json");

    private static readonly JsonDocument Hover = JsonDocument.Parse(
        Repo.ReadText(Repo.Path("editors", "vscode", "syntaxes", "nt65-hover.tmLanguage.json")));

    private readonly TempFolder root = new("nt65-extension-");

    public void Dispose() => root.Dispose();

    /// <summary>Every key the reader knows is in the schema, and the schema has no others.</summary>
    [Fact]
    public void TheSchemaHoldsTheKeysTheProjectFileReaderKnows()
    {
        Assert.Equal(
            ProjectFile.Keys.Order(StringComparer.Ordinal),
            Keys(Schema.RootElement).Order(StringComparer.Ordinal));
        Assert.Equal(
            ProjectFile.ConfigurationKeys.Order(StringComparer.Ordinal),
            Keys(Definition("configuration")).Order(StringComparer.Ordinal));
        Assert.Equal(
            ProjectFile.SegmentKeys.Order(StringComparer.Ordinal),
            Keys(Definition("segment")).Order(StringComparer.Ordinal));

        // nt65 reports any other key, so the schema forbids them and the editor flags one before
        // a build does.
        foreach (var element in (ReadOnlySpan<JsonElement>)[Schema.RootElement, Definition("configuration"), Definition("segment")])
            Assert.False(element.GetProperty("additionalProperties").GetBoolean());
    }

    /// <summary>
    /// The names the schema offers under <c>diagnostics</c> are exactly the catalogue's, and the
    /// values it offers for each are the levels the reader accepts. An editor completes them, so
    /// a name added to the catalogue and not to the schema would be offered nowhere.
    /// </summary>
    [Fact]
    public void TheSchemaOffersEveryDiagnosticByName()
    {
        var diagnostics = Definition("diagnostics");
        Assert.Equal(
            Catalogue.All.Select(descriptor => descriptor.Id),
            Enumeration(diagnostics.GetProperty("propertyNames")));
        Assert.Equal(ProjectFile.Levels, Enumeration(diagnostics.GetProperty("additionalProperties")));
    }

    /// <summary>The processors and the segment sizes the schema offers are the ones nt65 reads.</summary>
    [Fact]
    public void TheSchemaOffersTheProcessorsAndSizesNt65Reads()
    {
        Assert.Equal(
            CpuNames.All.Select(CpuNames.Format),
            Enumeration(Schema.RootElement.GetProperty("properties").GetProperty("cpu")));

        var sizes = Enumeration(Definition("segment").GetProperty("properties").GetProperty("size"));
        Assert.Equal(
            Enum.GetValues<AddressSize>().ToHashSet(),
            sizes.Select(size => SegmentNames.ParseSize(size) ?? throw new Xunit.Sdk.XunitException($"`{size}` is no size")).ToHashSet());
    }

    /// <summary>
    /// The <c>$nt65</c> problem matcher reads the file, the position and the severity of every
    /// diagnostic the command writes, and nothing from the lines that are not diagnostics.
    /// </summary>
    [Fact]
    public void TheProblemMatcherReadsWhatTheCommandWrites()
    {
        root.Write("nt65.json", """{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        root.Write("main.nt65", ".module main\n.segment CODE\n.proc main {\n    lda nowhere\n    rts\n}\n");

        var error = new StringWriter { NewLine = "\n" };
        Assert.Equal(1, Commands.Run(["build"], root.FullName, new StringWriter { NewLine = "\n" }, error,
            cancellation: TestTimeout.Token()));

        var printed = error.ToString().ReplaceLineEndings("\n").Split('\n').Where(line => line.Length > 0).ToList();
        var matcher = new Regex(Pattern("regexp"), RegexOptions.None, TimeSpan.FromSeconds(5));
        var matched = printed.Select(line => matcher.Match(line)).Where(match => match.Success).ToList();

        Assert.Equal(printed.Count, matched.Count);
        foreach (var match in matched)
        {
            Assert.Equal("main.nt65", match.Groups[int.Parse(Pattern("file"))].Value);
            Assert.True(int.Parse(match.Groups[int.Parse(Pattern("line"))].Value) > 0);
            Assert.True(int.Parse(match.Groups[int.Parse(Pattern("column"))].Value) > 0);
            Assert.Equal("error", match.Groups[int.Parse(Pattern("severity"))].Value);
            Assert.NotEmpty(match.Groups[int.Parse(Pattern("message"))].Value);
        }
        Assert.Contains(matched, match => match.Groups[5].Value.Contains("`nowhere` is not declared", StringComparison.Ordinal));

        // nt65's messages about itself have no position and do not match, so the Problems panel
        // holds only what is wrong with the program.
        Assert.DoesNotMatch(matcher, "nt65: no input files, and no nt65.json");
        Assert.DoesNotMatch(matcher, "nt65: deleted build/gone.s, which the program no longer writes");
    }

    /// <summary>The severities the matcher knows are the ones a diagnostic can have.</summary>
    [Fact]
    public void TheProblemMatcherKnowsEverySeverity()
    {
        var matcher = new Regex(Pattern("regexp"), RegexOptions.None, TimeSpan.FromSeconds(5));
        foreach (var severity in Enum.GetValues<Severity>())
        {
            var name = severity.ToString().ToLowerInvariant();
            Assert.Matches(matcher, $"src/main.nt65:12:5: {name}: something is wrong");
        }
    }

    /// <summary>
    /// Double-clicking selects a whole name, which includes a cheap local label with its
    /// <c>@</c>, a qualified name with its <c>::</c>, a directive with its <c>.</c> and a number
    /// with its <c>$</c>.
    /// </summary>
    [Fact]
    public void TheWordPatternTakesANameWhole()
    {
        var words = new Regex(
            Language.RootElement.GetProperty("wordPattern").GetString() ?? "", RegexOptions.None, TimeSpan.FromSeconds(5));
        foreach (var (line, whole) in (ReadOnlySpan<(string, string)>)[
            ("    jsr gfx::init", "gfx::init"),
            ("@loop:", "@loop"),
            (".proc main {", ".proc"),
            ("    lda #$d0_20", "$d0_20"),
            ("    lda #%1010", "%1010"),
            ("    lda actors[1]::hp", "actors")])
        {
            Assert.Contains(whole, words.Matches(line).Select(match => match.Value));
        }
    }

    /// <summary>The extension contributes the schema for the file nt65 reads, and its own matcher.</summary>
    [Fact]
    public void TheExtensionContributesTheSchemaAndTheMatcher()
    {
        var contributes = Package.RootElement.GetProperty("contributes");
        var validation = contributes.GetProperty("jsonValidation").EnumerateArray().Single();

        Assert.Equal(ProjectFile.Name, validation.GetProperty("fileMatch").GetString());
        Assert.Equal("./nt65.schema.json", validation.GetProperty("url").GetString());
        Assert.Equal("nt65", contributes.GetProperty("problemMatchers").EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal("nt65", contributes.GetProperty("taskDefinitions").EnumerateArray().Single().GetProperty("type").GetString());

        // nt65 reads the project file with comments and trailing commas allowed, so the editor
        // is told that the file is JSON with comments, not plain JSON. Otherwise the editor would
        // report errors in text that nt65 accepts.
        Assert.Equal(
            "jsonc",
            contributes.GetProperty("configurationDefaults").GetProperty("files.associations")
                .GetProperty(ProjectFile.Name).GetString());
    }

    /// <summary>
    /// Every command the client registers is one the palette offers, and every command the
    /// palette offers is one the client registers. The one exception is <c>nt65.rename</c>, which
    /// only the server invokes and nobody types.
    /// </summary>
    [Fact]
    public void TheCommandsOfferedAreTheCommandsRegistered()
    {
        var offered = Package.RootElement.GetProperty("contributes").GetProperty("commands")
            .EnumerateArray().Select(command => command.GetProperty("command").GetString() ?? "")
            .Order(StringComparer.Ordinal);
        var registered = Registered().Where(name => name != "nt65.rename").Order(StringComparer.Ordinal);
        Assert.Equal(offered, registered);

        // A lone file gets a server, because the extension starts on the language and not only
        // on a workspace that holds a project file.
        Assert.Contains(
            "onLanguage:nt65",
            Package.RootElement.GetProperty("activationEvents").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>
    /// Returns the commands the client's own code registers, found from the calls that register them.
    /// </summary>
    private static IEnumerable<string> Registered() =>
        ClientFiles
            .SelectMany(file => Regex.Matches(
                Repo.ReadText(Repo.Path("editors", "vscode", file)),
                @"registerCommand\('(?<name>[^']+)'",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)))
            .Select(match => match.Groups["name"].Value);

    /// <summary>
    /// The grammar that colours the grid in a hover is checked against the lines the server
    /// writes into it. Markdown formatting does not apply inside a fenced block, so the grid is
    /// fenced as a language of its own, and this grammar is the only thing that tells its parts
    /// apart. Nothing compiles the grammar or the server's text, so nothing else would notice
    /// them drifting apart.
    /// </summary>
    [Fact]
    public void TheHoverGrammarColoursTheGridTheServerWrites()
    {
        var contributes = Package.RootElement.GetProperty("contributes");
        Assert.Contains(
            contributes.GetProperty("languages").EnumerateArray(),
            language => language.GetProperty("id").GetString() == "nt65-hover");
        var grammar = contributes.GetProperty("grammars").EnumerateArray()
            .Single(item => item.GetProperty("language").GetString() == "nt65-hover");

        Assert.Equal("source.nt65-hover", grammar.GetProperty("scopeName").GetString());
        Assert.Equal("./syntaxes/nt65-hover.tmLanguage.json", grammar.GetProperty("path").GetString());
        Assert.Equal("source.nt65-hover", Hover.RootElement.GetProperty("scopeName").GetString());

        // The grammar colours the key of every row, whether it is one word, two, or a register.
        // It also colours the block total and the reason on a cycles row, which are about more
        // than the line, and anything the analysis could not work out. Everything else is left
        // plain.
        foreach (var (line, text, scope) in (ReadOnlySpan<(string, string, string?)>)[
            ("cycles  4-5       block 8-11    +1 when taken", "cycles", "entity.name.tag.nt65-hover"),
            ("cycles  4-5       block 8-11    +1 when taken", "4-5", "constant.numeric.nt65-hover"),
            ("cycles  4-5       block 8-11    +1 when taken", "block 8-11    +1 when taken", "comment.line.nt65-hover"),
            ("cycles  3", "3", "constant.numeric.nt65-hover"),
            ("cost       29-30 cycles", "cost", "entity.name.tag.nt65-hover"),
            ("private to  init", "private to", "entity.name.tag.nt65-hover"),
            ("flags   N V Z C", "flags", "entity.name.tag.nt65-hover"),
            ("flags   N V Z C", "N V Z C", null),
            ("state   a16, i8, native", "a16, i8, native", null),
            ("value    $0400 (1024)", "$0400", "constant.numeric.nt65-hover"),
            ("A       as entered, or new", "A", "entity.name.tag.nt65-hover"),
            ("A       as entered, or new", "as entered, or new", null),
            ("C       new, or unknown", "unknown", "invalid.deprecated.nt65-hover"),
            ("stack   unknown", "stack", "entity.name.tag.nt65-hover"),
            ("preserves  X, Y, ?", "preserves", "entity.name.tag.nt65-hover"),
            ("preserves  X, Y, ?", "?", "invalid.deprecated.nt65-hover"),
            ("preserves  A, X, Y, C", "A, X, Y, C", null),
            ("        status a16, i8", "status", null),
            ("        $1234", "$1234", "constant.numeric.nt65-hover"),
            ("        and 1 more", "and 1 more", "invalid.deprecated.nt65-hover")])
        {
            Assert.Equal(scope, Scoped(line, line.IndexOf(text, StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// Returns the scope the grammar gives the character at <paramref name="at"/>, or null where
    /// the grammar leaves it plain. Like TextMate, it takes the leftmost match, and where two
    /// rules would match at the same place, it takes the one listed first.
    /// </summary>
    private static string? Scoped(string line, int at)
    {
        for (var start = 0; start < line.Length;)
        {
            Match? found = null;
            var matched = default(JsonElement);
            foreach (var rule in Hover.RootElement.GetProperty("patterns").EnumerateArray())
            {
                var match = new Regex(rule.GetProperty("match").GetString() ?? "", RegexOptions.None, TimeSpan.FromSeconds(5))
                    .Match(line, start);
                if (match.Success && (found is null || match.Index < found.Index))
                    (found, matched) = (match, rule);
            }
            if (found is null)
                return null;
            if (at < found.Index || at >= found.Index + found.Length)
            {
                start = Math.Max(found.Index + found.Length, start + 1);
                continue;
            }
            if (matched.TryGetProperty("name", out var whole))
                return whole.GetString();
            foreach (var capture in matched.GetProperty("captures").EnumerateObject())
            {
                var group = found.Groups[int.Parse(capture.Name)];
                if (group.Success && at >= group.Index && at < group.Index + group.Length)
                    return capture.Value.GetProperty("name").GetString();
            }
            return null;
        }
        return null;
    }

    private static JsonDocument Read(string name) =>
        JsonDocument.Parse(Repo.ReadText(Repo.Path("editors", "vscode", name)));

    private static JsonElement Definition(string name) =>
        Schema.RootElement.GetProperty("definitions").GetProperty(name);

    private static IEnumerable<string> Keys(JsonElement element) =>
        element.GetProperty("properties").EnumerateObject().Select(property => property.Name);

    private static IEnumerable<string> Enumeration(JsonElement element) =>
        element.GetProperty("enum").EnumerateArray().Select(value => value.GetString() ?? "");

    /// <summary>Returns one field of the matcher's pattern, as text in the form the manifest gives it.</summary>
    private static string Pattern(string field)
    {
        var pattern = Package.RootElement.GetProperty("contributes")
            .GetProperty("problemMatchers").EnumerateArray().Single().GetProperty("pattern").GetProperty(field);
        return pattern.ValueKind == JsonValueKind.Number ? pattern.GetInt32().ToString() : pattern.GetString() ?? "";
    }
}
