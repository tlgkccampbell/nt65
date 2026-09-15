using System.Text.RegularExpressions;
using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Oracle;

/// <summary>Tests that run the pinned ca65. scripts/test.ps1 -Ca65 runs only these.</summary>
[Trait("Category", "Oracle")]
public sealed partial class OracleTests
{
    /// <summary>
    /// Hand-written ca65 files in tests/oracle. A trailing <c>;= N</c> on a line says ca65
    /// must generate N bytes for it; until nt65 computes lengths, that is what exercises
    /// the listing comparison.
    /// </summary>
    [Fact]
    public void HandWrittenFilesAssembleCleanly()
    {
        var files = Directory.GetFiles(Repo.Path("tests", "oracle"), "*.s").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        var failures = Repo.CollectFailures(files, Check);
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        static IEnumerable<string> Check(string path)
        {
            var name = Path.GetFileName(path);
            var source = Repo.ReadText(path);
            var result = Ca65Oracle.Pinned.Assemble(name, source);
            if (!result.Succeeded)
            {
                yield return $"{name}: ca65 reported:\n{result.Messages}";
                yield break;
            }
            var lines = source.ReplaceLineEndings("\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (ByteAnnotation().Match(lines[i]) is { Success: true } m
                    && int.Parse(m.Groups[1].Value) is var want && result.LineBytes[i] != want)
                {
                    yield return $"{name}:{i + 1}: expected {want} bytes, ca65 generated {result.LineBytes[i]}";
                }
            }
        }
    }

    /// <summary>Every expected output in the fixtures must assemble with no errors and no warnings.</summary>
    [Fact]
    public void FixtureOutputsAssembleCleanly()
    {
        var outputs = FixtureCase.All()
            .SelectMany(f => f.ExpectedOutputs().Select(o => (Fixture: f.Name, Path: o.Key, Text: o.Value)))
            .ToList();
        var failures = Repo.CollectFailures(outputs, o =>
        {
            // Lengths are compared against nt65's own once it computes them.
            var result = Ca65Oracle.Pinned.Assemble(Path.GetFileName(o.Path), o.Text);
            return result.Succeeded ? [] : [$"[{o.Fixture}] {o.Path}: ca65 reported:\n{result.Messages}"];
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void RefusesABuildThatIsNotThePinnedCommit()
    {
        var ca65 = Repo.Path(".cache", "cc65", "bin", OperatingSystem.IsWindows() ? "ca65.exe" : "ca65");
        var e = Assert.Throws<InvalidOperationException>(
            () => new Ca65Oracle(ca65, "547d9230000000000000000000000000000000", cacheDirectory: null));
        Assert.Contains("refusing to use ca65", e.Message);
    }

    [GeneratedRegex(@";=\s*(\d+)\s*$")]
    private static partial Regex ByteAnnotation();
}
