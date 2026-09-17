using System.Text.RegularExpressions;

namespace Norristown.Tests.Fixtures;

public sealed partial class IndentationTests
{
    /// <summary>
    /// What a line means never depends on where its first token starts. Every fixture, with
    /// every line indented further, reports the same diagnostics and writes the same output
    /// apart from whitespace and the source sizes it records.
    /// </summary>
    [Fact]
    public void IndentingEveryLineChangesNothing()
    {
        var fixtures = FixtureCase.All();
        var failures = Repo.CollectFailures(fixtures, Check);
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        static IEnumerable<string> Check(FixtureCase fixture)
        {
            var original = Compiler.Compile(fixture.Sources, fixture.Project, fixture.BinaryLength);
            var indented = Compiler.Compile(
                [.. fixture.Sources.Select(file => file with { Text = Indent(file.Text) })],
                fixture.Project, fixture.BinaryLength);

            var before = original.Diagnostics.Select(FixtureCase.Format).Order(StringComparer.Ordinal).ToList();
            var after = indented.Diagnostics.Select(FixtureCase.Format).Order(StringComparer.Ordinal).ToList();
            foreach (var lost in before.Except(after))
                yield return $"[{fixture.Name}] indented, no longer reports: {lost}";
            foreach (var gained in after.Except(before))
                yield return $"[{fixture.Name}] indented, also reports: {gained}";

            var outputs = indented.Outputs.ToDictionary(o => o.Path, StringComparer.Ordinal);
            foreach (var output in original.Outputs)
            {
                if (!outputs.TryGetValue(output.Path, out var other))
                {
                    yield return $"[{fixture.Name}] indented, writes no {output.Path}";
                    continue;
                }
                var want = Normalize(output.Text);
                var got = Normalize(other.Text);
                var at = Enumerable.Range(0, Math.Max(want.Count, got.Count))
                    .FirstOrDefault(i => i >= want.Count || i >= got.Count || want[i] != got[i], -1);
                if (at >= 0)
                {
                    yield return $"[{fixture.Name}] indented, {output.Path} differs:\n" +
                        $"  expected: {(at < want.Count ? want[at] : "<end>")}\n" +
                        $"  actual:   {(at < got.Count ? got[at] : "<end>")}";
                }
            }
        }
    }

    private static string Indent(string text) =>
        string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Length == 0 ? line : "    " + line));

    /// <summary>The lines that should match, which is all of them but the line map's source size.</summary>
    private static List<string> Normalize(string text) =>
        [.. text.Split('\n')
            .Where(line => !line.StartsWith("file ", StringComparison.Ordinal))
            .Select(line => Whitespace().Replace(line, ""))];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
