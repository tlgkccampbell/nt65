using System.Text.RegularExpressions;
using Norristown.Project;

namespace Norristown.Tests.Fixtures;

/// <summary>
/// Represents one build of a fixture. A fixture is a directory under <c>tests/fixtures</c> that
/// holds the following files.
/// <list type="bullet">
/// <item><c>**/*.nt65</c> is the program. Expected diagnostics are written inline as trailing
/// comments, such as <c>;! error[unused-symbol]: message</c>, on the line the diagnostic is
/// reported on. A diagnostic is matched on its line, its severity and its catalogue name. The
/// message is a second expectation, so that rewording a message is one line to change, and
/// <c>NT65_UPDATE=1</c> writes it.</item>
/// <item><c>nt65.json</c> is optional. Its own expected diagnostics are written in <c>//</c>
/// comments, which the reader skips.</item>
/// <item><c>expected/**</c> is the output snapshot, with one file per generated file at the
/// output's path. It holds the ca65 of each module and the line map beside it.</item>
/// </list>
/// <para>
/// A fixture may also be built more than one way. Each named configuration in its project file
/// is another build of the same sources, and so is each <c>nt65.<em>label</em>.json</c> beside
/// it, which covers what a configuration cannot change, such as the CPU. Each build has its
/// snapshot under <c>expected.<em>label</em></c>, so the differences between two builds of one
/// program can be read side by side in two snapshots. Every build must be clean, because an
/// inline <c>;!</c> does not say which configuration reports it.
/// </para>
/// </summary>
internal sealed partial record FixtureCase(
    string Name,
    string Directory,
    IReadOnlyList<SourceFile> Sources,
    ProjectSettings Project,
    SourceFile? ProjectFileText,
    string ExpectedDirectory)
{
    public const string DefaultExpectedDirectory = "expected";

    /// <summary>
    /// The lock held while updated annotations are written back to a fixture's files, which
    /// several cases may do at once.
    /// </summary>
    private static readonly Lock Writing = new();

    public static IReadOnlyList<FixtureCase> All()
    {
        var root = Repo.Path("tests", "fixtures");
        if (!System.IO.Directory.Exists(root))
            return [];

        // NT65_FIXTURE selects fixtures whose name contains the given text (scripts/test.ps1 -Fixture).
        var filter = Repo.Selection;
        return [.. System.IO.Directory.GetDirectories(root)
            .Select(dir => System.IO.Path.GetFileName(dir))
            .Where(name => string.IsNullOrEmpty(filter) || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .SelectMany(name => Load(System.IO.Path.Combine(root, name)))];
    }

    /// <summary>
    /// Returns every build of the fixture in <paramref name="directory"/>, one per configuration
    /// it holds.
    /// </summary>
    public static IReadOnlyList<FixtureCase> Load(string directory)
    {
        var sources = System.IO.Directory.GetFiles(directory, "*.nt65", SearchOption.AllDirectories)
            .Select(path => new SourceFile(RelativePath(directory, path), Repo.ReadText(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        var name = System.IO.Path.GetFileName(directory);

        var cases = new List<FixtureCase>();
        foreach (var json in System.IO.Directory.GetFiles(directory, "nt65*.json").Order(StringComparer.Ordinal))
        {
            // `nt65.json` is the fixture's own build; `nt65.<label>.json` is another one.
            var file = System.IO.Path.GetFileName(json);
            var label = file.Length > "nt65.json".Length ? file["nt65.".Length..^".json".Length] : "";

            var text = new SourceFile(file, Repo.ReadText(json));
            var project = Norristown.Project.ProjectFile.Read(Norristown.Project.ProjectFile.Name, text.Text);
            cases.Add(new FixtureCase(
                label.Length == 0 ? name : $"{name} ({label})",
                directory,
                sources,
                project,
                text,
                label.Length == 0 ? DefaultExpectedDirectory : $"{DefaultExpectedDirectory}.{label}"));
            if (label.Length > 0)
                continue;
            foreach (var configuration in project.Configurations)
            {
                cases.Add(new FixtureCase(
                    $"{name} ({configuration.Name})",
                    directory,
                    sources,
                    project.Configured(configuration.Name, default),
                    text,
                    $"{DefaultExpectedDirectory}.{configuration.Name}"));
            }
        }
        if (cases.Count == 0)
        {
            cases.Add(new FixtureCase(
                name, directory, sources, ProjectSettings.None, null, DefaultExpectedDirectory));
        }
        return cases;
    }

    public static string RelativePath(string directory, string path) =>
        System.IO.Path.GetRelativePath(directory, path).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    public static IEnumerable<Expectation> ParseInlineDiagnostics(SourceFile file)
    {
        var lines = file.Text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // A `;!` inside a string literal would be misread; fixtures should not do that.
            foreach (Match m in InlineDiagnostic().Matches(lines[i]))
            {
                yield return new Expectation(
                    file.Path,
                    i + 1,
                    m.Groups["severity"].Value,
                    m.Groups["id"].Value,
                    m.Groups["message"].Value.Trim());
            }
        }
    }

    /// <summary>Returns the expectation that matches a reported diagnostic. Columns are not checked.</summary>
    public static Expectation Of(Diagnostic d) =>
        new(d.Span.File, d.Span.Line, d.Severity.ToString().ToLowerInvariant(), d.Id, d.Message);

    /// <summary>
    /// Formats a diagnostic as a string, for checks that only ask whether two sets of diagnostics
    /// are the same.
    /// </summary>
    public static string Format(Diagnostic d) => Of(d).ToString();

    /// <summary>
    /// Returns the length of a file an <c>.incbin</c> names, found in the fixture's directory. A
    /// fixture's binaries sit beside its sources, no matter where the tests are run from.
    /// </summary>
    public long? BinaryLength(string path)
    {
        var file = System.IO.Path.Combine(Directory, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return File.Exists(file) ? new FileInfo(file).Length : null;
    }

    /// <summary>
    /// Returns every <c>.bin</c> file in the fixture, at its path relative to the fixture. The
    /// generated file looks for it at that same path relative to its own.
    /// </summary>
    public IReadOnlyList<(string Name, byte[] Content)> Binaries() =>
        [.. System.IO.Directory.GetFiles(Directory, "*.bin", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (RelativePath(Directory, path), File.ReadAllBytes(path)))];

    /// <summary>Returns the expected output, keyed by output path.</summary>
    public SortedDictionary<string, string> ExpectedOutputs()
    {
        var dir = System.IO.Path.Combine(Directory, ExpectedDirectory);
        var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (System.IO.Directory.Exists(dir))
        {
            foreach (var path in System.IO.Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                outputs[RelativePath(dir, path)] = Repo.ReadText(path).ReplaceLineEndings("\n");
        }
        return outputs;
    }

    /// <summary>Returns the diagnostics that the inline <c>;!</c> comments expect the program to report.</summary>
    public List<Expectation> ExpectedDiagnostics() =>
        [.. Annotated().SelectMany(ParseInlineDiagnostics)];

    /// <summary>
    /// Writes the messages in <paramref name="actual"/> into the existing annotations that match
    /// them, so that rewording a message needs no hand edit to the fixture. An annotation is
    /// rewritten only where exactly one diagnostic on its line is reported with its severity and
    /// name. Any other mismatch is a difference the fixture should fail on.
    /// </summary>
    public List<Expectation> UpdateInlineDiagnostics(IReadOnlyList<Expectation> actual)
    {
        var expected = new List<Expectation>();
        foreach (var file in Annotated())
        {
            var lines = file.Text.ReplaceLineEndings("\n").Split('\n');
            var changed = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = i + 1;
                var written = InlineDiagnostic().Replace(lines[i], match =>
                {
                    var found = actual.Where(d => d.File == file.Path && d.Line == line
                        && d.Severity == match.Groups["severity"].Value
                        && d.Id == match.Groups["id"].Value)
                        .ToList();
                    return found is [var one] ? $";! {one.Severity}[{one.Id}]: {one.Message}" : match.Value;
                });
                if (written != lines[i])
                {
                    lines[i] = written;
                    changed = true;
                }
                expected.AddRange(ParseInlineDiagnostics(new SourceFile(file.Path, lines[i]))
                    .Select(said => said with { Line = line }));
            }
            if (!changed)
                continue;

            // A fixture built more than one way is several cases over the same files, run in
            // parallel. Each of them writes the same text back, so the writes take a lock.
            var path = System.IO.Path.Combine(Directory, file.Path);
            var whole = string.Join('\n', lines);
            lock (Writing)
            {
                if (Repo.ReadText(path).ReplaceLineEndings("\n") != whole)
                    Repo.WriteText(path, whole);
            }
        }
        return expected;
    }

    /// <summary>
    /// Returns the files a fixture may contain annotations in, which are its sources and its
    /// project file.
    /// </summary>
    private IEnumerable<SourceFile> Annotated() =>
        Sources.Concat(ProjectFileText is null ? [] : [ProjectFileText]);

    // A line may hold more than one annotation, so a message runs to the next `;!` rather than
    // to the end of the line. The `;!` marker delimits them, not a bare `;`, so a message may
    // contain a `;`, as in "`COUNTR` is not declared; `COUNTER` is".
    [GeneratedRegex(@";!\s*(?<severity>error|warning|info)\s*\[(?<id>[a-z0-9-]+)\]\s*:(?<message>(?:(?!;!).)*)")]
    private static partial Regex InlineDiagnostic();

    /// <summary>
    /// Represents one diagnostic as a fixture expects it: its location, the name it is reported
    /// under, and its message. A diagnostic is matched on its name and location. The message is a
    /// second expectation, so that a reworded message fails as one difference, not as one missing
    /// and one unexpected diagnostic.
    /// </summary>
    /// <param name="File">The file the diagnostic is reported in.</param>
    /// <param name="Line">The 1-based line the diagnostic is reported on.</param>
    /// <param name="Severity">The severity, in lower case as the annotation writes it.</param>
    /// <param name="Id">The catalogue name.</param>
    /// <param name="Message">The diagnostic's message.</param>
    internal readonly record struct Expectation(string File, int Line, string Severity, string Id, string Message)
    {
        /// <summary>
        /// Gets everything but the message, which is the part a diagnostic is matched on.
        /// </summary>
        public string Where => $"{File}:{Line}: {Severity}[{Id}]";

        public override string ToString() => $"{Where}: {Message}";
    }
}
