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
            .SelectMany(fixture => Compiler.Compile(fixture.Sources, fixture.Project).Outputs.Select(o => (fixture.Name, Output: o)))
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

    /// <summary>
    /// A fixture with a <c>link/</c> directory holds a hand-written ca65 module and a linker
    /// configuration. nt65's output must link against it: the object file is the only
    /// boundary between the two (§12), so what nt65 exports has to be what ca65 imports and
    /// the other way round.
    /// </summary>
    [Fact]
    public void GeneratedOutputLinksWithHandWrittenCa65()
    {
        var linkable = FixtureCase.All().Where(fixture => LinkFiles(fixture) is not null).ToList();
        Assert.NotEmpty(linkable);

        var failures = Repo.CollectFailures(linkable, Check);
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        static IEnumerable<string> Check(FixtureCase fixture)
        {
            var (config, handWritten) = LinkFiles(fixture)!.Value;
            var compilation = Compiler.Compile(fixture.Sources, fixture.Project);
            var result = Ca65Oracle.Pinned.Link(config,
                [.. compilation.Outputs.Select(o => (Path.GetFileName(o.Path), o.Text)), .. handWritten]);
            if (!result.Succeeded)
                yield return $"[{fixture.Name}] ld65 reported:\n{result.Messages}";
            else if (result.Binary.Length == 0)
                yield return $"[{fixture.Name}] linked, but wrote no bytes";
        }
    }

    /// <summary>
    /// A checked import is a promise nt65 made about a value it has already used in its own
    /// arithmetic, and the linker is what keeps it: link the same program against a module
    /// that defines the symbol differently and ld65 must refuse it (§12).
    /// </summary>
    [Fact]
    public void ACheckedImportWithTheWrongValueFailsTheLink()
    {
        var fixture = FixtureCase.All().Single(f => f.Name == "modules");
        var (config, handWritten) = LinkFiles(fixture)!.Value;
        var wrong = handWritten
            .Select(file => (file.Name, Source: file.Source.Replace("HOST_VERSION = $0102", "HOST_VERSION = $0103")))
            .ToList();
        Assert.Contains(wrong, file => file.Source.Contains("$0103"));

        var result = Ca65Oracle.Pinned.Link(config,
            [.. Compiler.Compile(fixture.Sources, fixture.Project).Outputs
                .Select(o => (Path.GetFileName(o.Path), o.Text)),
             .. wrong]);

        Assert.False(result.Succeeded);
        Assert.Contains("HOST_VERSION is not $0102", result.Messages);
    }

    /// <summary>The linker configuration and the hand-written modules of a fixture, or null.</summary>
    private static (string Config, IReadOnlyList<(string Name, string Source)> Modules)? LinkFiles(FixtureCase fixture)
    {
        var directory = Path.Combine(fixture.Directory, "link");
        if (!Directory.Exists(directory))
            return null;
        var config = Path.Combine(directory, "link.cfg");
        if (!File.Exists(config))
            return null;
        return (Repo.ReadText(config), [.. Directory.GetFiles(directory, "*.s")
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), Repo.ReadText(path)))]);
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
