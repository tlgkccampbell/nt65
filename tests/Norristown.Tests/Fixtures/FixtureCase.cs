using System.Text.RegularExpressions;

namespace Norristown.Tests.Fixtures;

/// <summary>
/// A fixture is a directory under <c>tests/fixtures</c>:
/// <list type="bullet">
/// <item><c>**/*.nt65</c>, the program, with expected diagnostics written inline as trailing
/// comments: <c>;! error: message</c> on the line the diagnostic is reported on;</item>
/// <item><c>nt65.json</c>, optional and not read yet;</item>
/// <item><c>expected/**/*.s</c>, the output snapshot, one file per generated file, at the
/// output's path.</item>
/// </list>
/// </summary>
internal sealed partial record FixtureCase(string Name, string Directory, IReadOnlyList<SourceFile> Sources)
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
        return new FixtureCase(System.IO.Path.GetFileName(directory), directory, sources);
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
        [.. Sources.SelectMany(ParseInlineDiagnostics).Order(StringComparer.Ordinal)];

    // A line may carry more than one annotation, so a message runs to the next `;` rather
    // than to the end of the line; no diagnostic nt65 writes contains one.
    [GeneratedRegex(@";!\s*(?<severity>error|warning|info)\s*:(?<message>[^;]*)")]
    private static partial Regex InlineDiagnostic();
}
