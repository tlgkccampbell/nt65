using System.Text.RegularExpressions;
using Norristown.Project;

namespace Norristown.Tests.Fixtures;

/// <summary>
/// A fixture is a directory under <c>tests/fixtures</c>:
/// <list type="bullet">
/// <item><c>**/*.nt65</c>, the program, with expected diagnostics written inline as trailing
/// comments: <c>;! error: message</c> on the line the diagnostic is reported on;</item>
/// <item><c>nt65.json</c>, optional. Its own expected diagnostics are written in
/// <c>//</c> comments, which the reader skips;</item>
/// <item><c>expected/**/*.s</c>, the output snapshot, one file per generated file, at the
/// output's path.</item>
/// </list>
/// </summary>
internal sealed partial record FixtureCase(
    string Name,
    string Directory,
    IReadOnlyList<SourceFile> Sources,
    ProjectSettings Project,
    SourceFile? ProjectFileText)
{
    public const string ExpectedDirectory = "expected";

    public static IReadOnlyList<FixtureCase> All()
    {
        var root = Repo.Path("tests", "fixtures");
        if (!System.IO.Directory.Exists(root))
            return [];

        // NT65_FIXTURE selects fixtures whose name contains the given text (scripts/test.ps1 -Fixture).
        var filter = Environment.GetEnvironmentVariable("NT65_FIXTURE");
        return [.. System.IO.Directory.GetDirectories(root)
            .Select(dir => System.IO.Path.GetFileName(dir))
            .Where(name => string.IsNullOrEmpty(filter) || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(name => Load(System.IO.Path.Combine(root, name)))];
    }

    public static FixtureCase Load(string directory)
    {
        var sources = System.IO.Directory.GetFiles(directory, "*.nt65", SearchOption.AllDirectories)
            .Select(path => new SourceFile(RelativePath(directory, path), Repo.ReadText(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        var project = ProjectSettings.None;
        SourceFile? projectFile = null;
        var json = System.IO.Path.Combine(directory, Norristown.Project.ProjectFile.Name);
        if (File.Exists(json))
        {
            projectFile = new SourceFile(Norristown.Project.ProjectFile.Name, Repo.ReadText(json));
            project = Norristown.Project.ProjectFile.Read(projectFile.Path, projectFile.Text);
        }
        return new FixtureCase(
            System.IO.Path.GetFileName(directory), directory, sources, project, projectFile);
    }

    public static string RelativePath(string directory, string path) =>
        System.IO.Path.GetRelativePath(directory, path).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    public static IEnumerable<string> ParseInlineDiagnostics(SourceFile file)
    {
        var lines = file.Text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // A `;!` inside a string literal would be misread; fixtures should not do that.
            foreach (Match m in InlineDiagnostic().Matches(lines[i]))
                yield return $"{file.Path}:{i + 1}: {m.Groups["severity"].Value}: {m.Groups["message"].Value.Trim()}";
        }
    }

    /// <summary>The comparison form: file, line, severity and message. Columns are not checked.</summary>
    public static string Format(Diagnostic d) =>
        $"{d.Span.File}:{d.Span.Line}: {d.Severity.ToString().ToLowerInvariant()}: {d.Message}";

    /// <summary>
    /// How long a file an <c>.incbin</c> names is. A fixture's binaries sit beside its
    /// sources, wherever the tests happen to be run from.
    /// </summary>
    public long? BinaryLength(string path)
    {
        var file = System.IO.Path.Combine(Directory, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return File.Exists(file) ? new FileInfo(file).Length : null;
    }

    /// <summary>
    /// The binaries a fixture's sources name, for an assembler that has to find them beside
    /// the generated file rather than beside the fixture.
    /// </summary>
    public IReadOnlyList<(string Name, byte[] Content)> Binaries() =>
        [.. System.IO.Directory.GetFiles(Directory, "*.bin")
            .Order(StringComparer.Ordinal)
            .Select(path => (System.IO.Path.GetFileName(path), File.ReadAllBytes(path)))];

    /// <summary>Expected output, keyed by output path.</summary>
    public SortedDictionary<string, string> ExpectedOutputs()
    {
        var dir = System.IO.Path.Combine(Directory, ExpectedDirectory);
        var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (System.IO.Directory.Exists(dir))
        {
            foreach (var path in System.IO.Directory.GetFiles(dir, "*.s", SearchOption.AllDirectories))
                outputs[RelativePath(dir, path)] = Repo.ReadText(path).ReplaceLineEndings("\n");
        }
        return outputs;
    }

    /// <summary>Expected diagnostics from the inline <c>;!</c> comments, as formatted by <see cref="Format"/>.</summary>
    public List<string> ExpectedDiagnostics() =>
        [.. Sources.Concat(ProjectFileText is null ? [] : new[] { ProjectFileText })
            .SelectMany(ParseInlineDiagnostics)
            .Order(StringComparer.Ordinal)];

    // A line may carry more than one annotation, so a message runs to the next `;` rather
    // than to the end of the line; no diagnostic nt65 writes contains one.
    [GeneratedRegex(@";!\s*(?<severity>error|warning|info)\s*:(?<message>[^;]*)")]
    private static partial Regex InlineDiagnostic();
}
