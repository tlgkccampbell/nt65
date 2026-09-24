using Norristown.Emit;
using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Oracle;

/// <summary>
/// Tests what a debugger is told, end to end. The tests assemble nt65's output with the pinned
/// <c>ca65 -g</c>, link it with <c>ld65 --dbgfile</c>, and remap the debug file from the line
/// maps. nt65 writes no <c>.dbg</c> directives, so this is the only test that checks that the
/// bytes can be traced back to the <c>.nt65</c> they came from.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DebugFileTests
{
    /// <summary>
    /// Every line of a <c>.s</c> that its line map covers and that ld65 recorded bytes for is also
    /// recorded, after remapping, as the source line it came from, with the same spans. Nothing
    /// the assembler generated is lost on the way, and nothing is claimed for a line that
    /// generated none.
    /// </summary>
    [Fact]
    public void RemappingNamesTheSourceOfEveryMappedLineThatMadeBytes()
    {
        var linkable = Linkable();
        Repo.RequireAny(linkable);
        var failures = Repo.CollectFailures(linkable, Check);
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        static IEnumerable<string> Check(FixtureCase fixture)
        {
            var compilation = Compiler.Compile(fixture.Sources, fixture.Project);
            var maps = compilation.Outputs
                .Where(output => output.Kind == OutputKind.LineMap)
                .ToDictionary(output => Path.GetFileName(output.Path), output => output.Text, StringComparer.Ordinal);
            var result = Ca65Oracle.Pinned.Link(
                LinkConfig(fixture),
                [.. compilation.Ca65.Select(o => (Path.GetFileName(o.Path), o.Text)), .. HandWritten(fixture)],
                debugFile: true);
            if (!result.Succeeded)
            {
                yield return $"[{fixture.Name}] ld65 reported:\n{result.Messages}";
                yield break;
            }

            var remapped = DebugFile.Remap(result.DebugFile, name => maps.GetValueOrDefault(name + LineMap.Extension),
                out var problem);
            if (remapped is null)
            {
                yield return $"[{fixture.Name}] the debug file could not be remapped: {problem}";
                yield break;
            }

            // For each `.s` line, the spans ld65 recorded for it and the source line its map
            // gives for it are checked against the spans the remapped file gives that source line.
            var files = Files(result.DebugFile);
            var after = Records(remapped, "line")
                .Where(record => record.TryGetValue("type", out var type) && type == "1")
                .ToLookup(record => (record["file"], record["line"]), record => Spans(record));
            foreach (var record in Records(result.DebugFile, "line"))
            {
                if (!files.TryGetValue(record["file"], out var name) || !maps.ContainsKey(name + LineMap.Extension))
                    continue;
                var spans = Spans(record);
                if (spans.Count == 0)
                    continue;
                var map = LineMap.Read(maps[name + LineMap.Extension], out _)!;
                if (!map.Lines.TryGetValue(int.Parse(record["line"]), out var from))
                {
                    yield return $"[{fixture.Name}] {name}:{record["line"]} covers {spans.Count} span(s), "
                        + "and the line map says nothing about it";
                    continue;
                }
                var source = map.Sources[from.File].Path;
                var named = after[(Id(remapped, source), from.Line.ToString())].SelectMany(set => set).ToHashSet();
                foreach (var lost in spans.Where(span => !named.Contains(span)))
                    yield return $"[{fixture.Name}] {source}:{from.Line} lost span {lost}, from {name}:{record["line"]}";
            }
        }
    }

    /// <summary>
    /// Remapping an already remapped debug file changes nothing, so a build that repeats the step
    /// is safe.
    /// </summary>
    [Fact]
    public void RemappingWhatIsAlreadyRemappedChangesNothing()
    {
        Repo.SkipUnlessSelected("modules");
        var fixture = Linkable().Single(f => f.Name == "modules");
        var compilation = Compiler.Compile(fixture.Sources, fixture.Project);
        var maps = compilation.Outputs
            .Where(output => output.Kind == OutputKind.LineMap)
            .ToDictionary(output => Path.GetFileName(output.Path), output => output.Text, StringComparer.Ordinal);
        var result = Ca65Oracle.Pinned.Link(
            LinkConfig(fixture),
            [.. compilation.Ca65.Select(o => (Path.GetFileName(o.Path), o.Text)), .. HandWritten(fixture)],
            debugFile: true);
        Assert.True(result.Succeeded, result.Messages);

        string? Remap(string text) =>
            DebugFile.Remap(text, name => maps.GetValueOrDefault(name + LineMap.Extension), out _);

        var once = Remap(result.DebugFile);
        Assert.NotNull(once);
        Assert.NotEqual(result.DebugFile, once);
        Assert.Equal(once, Remap(once));
    }

    /// <summary>
    /// A translation unit of several modules assembles to one object, which the remapped debug
    /// file attributes to the root module's source. Each placed module's lines still name its own
    /// source, so that stepping into a placed routine shows the file it was written in.
    /// </summary>
    [Fact]
    public void APlacedModulesLinesNameItsOwnSource()
    {
        Repo.SkipUnlessSelected("placement");
        var fixture = Linkable().Single(f => f.Name == "placement");
        var compilation = Compiler.Compile(fixture.Sources, fixture.Project);
        var maps = compilation.Outputs
            .Where(output => output.Kind == OutputKind.LineMap)
            .ToDictionary(output => Path.GetFileName(output.Path), output => output.Text, StringComparer.Ordinal);
        var result = Ca65Oracle.Pinned.Link(
            LinkConfig(fixture), [.. compilation.Ca65.Select(o => (Path.GetFileName(o.Path), o.Text))], debugFile: true);
        Assert.True(result.Succeeded, result.Messages);
        var remapped = DebugFile.Remap(result.DebugFile, name => maps.GetValueOrDefault(name + LineMap.Extension), out var problem);
        Assert.True(remapped is not null, problem);

        // The object assembled from main.s is attributed to main's source, and every source in
        // its translation unit has line records of its own.
        var module = Records(remapped, "mod").Single(record => record["name"] == "main.o");
        Assert.Equal("main.nt65", Files(remapped)[module["file"]]);
        var lines = Records(remapped, "line").Where(record => record.GetValueOrDefault("type") == "1").ToList();
        foreach (var source in new[] { "main.nt65", "tables.nt65", "flow1.nt65", "iscntc.nt65" })
            Assert.Contains(lines, record => record["file"] == Id(remapped, source));
    }

    /// <summary>Returns the fixtures that hold a linker configuration, which are the ones that can link.</summary>
    private static IReadOnlyList<FixtureCase> Linkable() =>
        [.. FixtureCase.All().Where(fixture => File.Exists(LinkConfigPath(fixture)))];

    private static string LinkConfig(FixtureCase fixture) => Repo.ReadText(LinkConfigPath(fixture));

    private static string LinkConfigPath(FixtureCase fixture) =>
        Path.Combine(fixture.Directory, "link", "link.cfg");

    /// <summary>Returns the hand-written ca65 modules that a fixture links its output against.</summary>
    private static IReadOnlyList<(string Name, string Source)> HandWritten(FixtureCase fixture) =>
        [.. Directory.GetFiles(Path.Combine(fixture.Directory, "link"), "*.s")
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), Repo.ReadText(path)))];

    /// <summary>Returns the <c>file</c> records of a debug file, mapping each id to its file name.</summary>
    private static Dictionary<string, string> Files(string text) =>
        Records(text, "file").ToDictionary(record => record["id"], record => record["name"]);

    /// <summary>Returns the id that the debug file gives the file named <paramref name="name"/>.</summary>
    private static string Id(string text, string name) =>
        Records(text, "file").First(record => record["name"] == name)["id"];

    /// <summary>Returns the span ids that a <c>line</c> record carries, which may be none.</summary>
    private static HashSet<string> Spans(IReadOnlyDictionary<string, string> record) =>
        record.TryGetValue("span", out var spans) ? [.. spans.Split('+')] : [];

    /// <summary>
    /// Returns every record of one kind, each as a dictionary of its fields, with the quotes
    /// removed from the values.
    /// </summary>
    private static List<Dictionary<string, string>> Records(string text, string keyword) =>
        [.. text.ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.StartsWith(keyword + "\t", StringComparison.Ordinal))
            .Select(line => line[(keyword.Length + 1)..].Split(',')
                .Select(field => field.Split('=', 2))
                .Where(field => field.Length == 2)
                .ToDictionary(field => field[0], field => field[1].Trim('"')))];
}
