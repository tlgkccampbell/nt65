using System.Text.Json;
using System.Text.RegularExpressions;
using Norristown.Cli;
using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Tests.Editors;

/// <summary>
/// What the VS Code extension contributes, checked against what nt65 itself does: the schema
/// for the project file against the reader, and the problem matcher against the lines
/// <c>nt65 build</c> writes. Both are data files nothing compiles, so nothing else would
/// notice them drifting.
/// </summary>
public sealed class ExtensionTests : IDisposable
{
    private static readonly JsonDocument Package = Read("package.json");
    private static readonly JsonDocument Schema = Read("nt65.schema.json");
    private static readonly JsonDocument Language = Read("language-configuration.json");

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nt65-extension-");

    public void Dispose() => root.Delete(recursive: true);

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

        // Anything else is a key nt65 reports, so the editor says so before the build does.
        foreach (var element in (ReadOnlySpan<JsonElement>)[Schema.RootElement, Definition("configuration"), Definition("segment")])
            Assert.False(element.GetProperty("additionalProperties").GetBoolean());
    }

    /// <summary>The processors and the segment sizes the schema offers are the ones nt65 reads.</summary>
    [Fact]
    public void TheSchemaOffersTheProcessorsAndSizesNt65Reads()
    {
        Assert.Equal(
            CpuNames.All.Select(CpuNames.Spell),
            Enumeration(Schema.RootElement.GetProperty("properties").GetProperty("cpu")));

        var sizes = Enumeration(Definition("segment").GetProperty("properties").GetProperty("size"));
        Assert.Equal(
            Enum.GetValues<AddressSize>().ToHashSet(),
            sizes.Select(written => SegmentNames.ParseSize(written) ?? throw new Xunit.Sdk.XunitException($"`{written}` is no size")).ToHashSet());
    }

    /// <summary>
    /// The <c>$nt65</c> problem matcher reads what the command writes: the file, the position
    /// and the severity of every diagnostic, and nothing of the lines that are not diagnostics.
    /// </summary>
    [Fact]
    public void TheProblemMatcherReadsWhatTheCommandWrites()
    {
        Write("nt65.json", """{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        Write("main.nt65", ".module main\n.segment CODE\n.proc main {\n    lda nowhere\n    rts\n}\n");

        var error = new StringWriter { NewLine = "\n" };
        Assert.Equal(1, Commands.Run(["build"], root.FullName, new StringWriter { NewLine = "\n" }, error));

        var said = error.ToString().ReplaceLineEndings("\n").Split('\n').Where(line => line.Length > 0).ToList();
        var matcher = new Regex(Pattern("regexp"), RegexOptions.None, TimeSpan.FromSeconds(5));
        var matched = said.Select(line => matcher.Match(line)).Where(match => match.Success).ToList();

        Assert.Equal(said.Count, matched.Count);
        foreach (var match in matched)
        {
            Assert.Equal("main.nt65", match.Groups[int.Parse(Pattern("file"))].Value);
            Assert.True(int.Parse(match.Groups[int.Parse(Pattern("line"))].Value) > 0);
            Assert.True(int.Parse(match.Groups[int.Parse(Pattern("column"))].Value) > 0);
            Assert.Equal("error", match.Groups[int.Parse(Pattern("severity"))].Value);
            Assert.NotEmpty(match.Groups[int.Parse(Pattern("message"))].Value);
        }
        Assert.Contains(matched, match => match.Groups[5].Value.Contains("`nowhere` is not declared", StringComparison.Ordinal));

        // What the command says about itself has no position, so the Problems panel is left
        // holding only what is wrong with the program.
        Assert.DoesNotMatch(matcher, "nt65: no input files, and no nt65.json");
        Assert.DoesNotMatch(matcher, "nt65: deleted build/gone.s, whose module is not in the program");
    }

    /// <summary>The severities the matcher knows are the ones a diagnostic can have.</summary>
    [Fact]
    public void TheProblemMatcherKnowsEverySeverity()
    {
        var matcher = new Regex(Pattern("regexp"), RegexOptions.None, TimeSpan.FromSeconds(5));
        foreach (var severity in Enum.GetValues<Severity>())
        {
            var written = severity.ToString().ToLowerInvariant();
            Assert.Matches(matcher, $"src/main.nt65:12:5: {written}: something is wrong");
        }
    }

    /// <summary>
    /// A double-click takes a name whole: a cheap local with its <c>@</c>, a path with its
    /// <c>::</c>, a directive with its <c>.</c> and a number with its <c>$</c>.
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
    }

    private static JsonDocument Read(string name) =>
        JsonDocument.Parse(Repo.ReadText(Repo.Path("editors", "vscode", name)));

    private static JsonElement Definition(string name) =>
        Schema.RootElement.GetProperty("definitions").GetProperty(name);

    private static IEnumerable<string> Keys(JsonElement element) =>
        element.GetProperty("properties").EnumerateObject().Select(property => property.Name);

    private static IEnumerable<string> Enumeration(JsonElement element) =>
        element.GetProperty("enum").EnumerateArray().Select(value => value.GetString() ?? "");

    /// <summary>One field of the matcher's pattern, as the manifest writes it.</summary>
    private static string Pattern(string field)
    {
        var pattern = Package.RootElement.GetProperty("contributes")
            .GetProperty("problemMatchers").EnumerateArray().Single().GetProperty("pattern").GetProperty(field);
        return pattern.ValueKind == JsonValueKind.Number ? pattern.GetInt32().ToString() : pattern.GetString() ?? "";
    }

    private void Write(string path, string text) => Repo.WriteText(Path.Combine(root.FullName, path), text);
}
