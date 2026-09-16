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

    /// <summary>
    /// Everything nt65 generates must assemble with no errors and no warnings — a ca65
    /// diagnostic on nt65 output is an nt65 bug (§3.2) — and ca65 must generate exactly as
    /// many bytes for each line as nt65 worked out for it (§7.6). Those lengths are what
    /// branch range, cycle counts and assertions will be built on, so agreeing with the
    /// assembler about them is the check that matters.
    /// </summary>
    [Fact]
    public void GeneratedOutputAssemblesToTheLengthsNt65Computed()
    {
        var outputs = FixtureCase.All()
            .SelectMany(fixture => Compiler.Compile(fixture.Sources).Outputs.Select(o => (fixture.Name, Output: o)))
            .ToList();
        Assert.NotEmpty(outputs);
        Assert.Contains(outputs, o => o.Output.LineBytes.Any(bytes => bytes > 0));

        var failures = Repo.CollectFailures(outputs, o => Check(o.Name, o.Output));
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        static IEnumerable<string> Check(string fixture, OutputFile output)
        {
            var result = Ca65Oracle.Pinned.Assemble(Path.GetFileName(output.Path), output.Text);
            if (!result.Succeeded)
            {
                yield return $"[{fixture}] {output.Path}: ca65 reported:\n{result.Messages}";
                yield break;
            }

            var lines = output.Text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
            if (output.LineBytes.Count != lines.Length)
            {
                yield return $"[{fixture}] {output.Path}: {lines.Length} lines, but nt65 has lengths for " +
                    $"{output.LineBytes.Count}";
                yield break;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                if (result.LineBytes[i] != output.LineBytes[i])
                {
                    yield return $"[{fixture}] {output.Path}:{i + 1}: nt65 says {output.LineBytes[i]} bytes, " +
                        $"ca65 generated {result.LineBytes[i]}:\n  {lines[i]}";
                }
            }
        }
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
