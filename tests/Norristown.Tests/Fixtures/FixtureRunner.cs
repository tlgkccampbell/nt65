using Norristown.Syntax;

namespace Norristown.Tests.Fixtures;

internal static class FixtureRunner
{
    /// <summary>True when NT65_UPDATE is set: expected output is rewritten instead of compared.</summary>
    public static bool UpdateMode => Environment.GetEnvironmentVariable("NT65_UPDATE") is "1" or "true";

    /// <summary>Runs one fixture and returns its failures, empty when it passes.</summary>
    public static IEnumerable<string> Run(FixtureCase fixture, bool update = false) =>
        Run(fixture, sources => Compiler.Compile(sources, fixture.Project, fixture.BinaryLength), update);

    public static IEnumerable<string> Run(
        FixtureCase fixture, Func<IReadOnlyCollection<SourceFile>, Compilation> compile, bool update = false)
    {
        var failures = new List<string>();
        void Fail(string message) => failures.Add($"[{fixture.Name}] {message}");

        var compilation = compile(fixture.Sources);

        // Full fidelity, on every fixture: the tree and each statement read back as the source.
        foreach (var file in fixture.Sources)
        {
            foreach (var problem in Syntax.Fidelity.Problems(SyntaxTree.Parse(file)))
                Fail($"{file.Path}: {problem}");
        }

        // Output is deterministic: the same sources give byte-identical output, and a
        // program is a set of files, so other orders must give identical results too.
        if (!Same(compilation, compile(fixture.Sources)))
            Fail("output or diagnostics change between two runs over the same files");
        foreach (var (label, order) in OtherOrders(fixture.Sources))
        {
            if (!Same(compilation, compile(order)))
                Fail($"output or diagnostics change when the files are {label}");
        }

        var expectedDiagnostics = fixture.ExpectedDiagnostics();
        var actualDiagnostics = compilation.Diagnostics
            .Select(FixtureCase.Format)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var missing in expectedDiagnostics.Except(actualDiagnostics))
            Fail($"expected diagnostic not reported: {missing}");
        foreach (var extra in actualDiagnostics.Except(expectedDiagnostics))
            Fail($"unexpected diagnostic: {extra}");

        var expected = fixture.ExpectedOutputs();
        var actual = compilation.Outputs.ToDictionary(o => o.Path, o => o.Text, StringComparer.Ordinal);
        var expectedDir = Path.Combine(fixture.Directory, fixture.ExpectedDirectory);

        if (update)
        {
            foreach (var path in expected.Keys.Except(actual.Keys))
                File.Delete(Path.Combine(expectedDir, path));
            foreach (var (path, text) in actual)
            {
                if (!expected.TryGetValue(path, out var old) || old != text)
                    Repo.WriteText(Path.Combine(expectedDir, path), text);
            }
            return failures;
        }

        foreach (var path in expected.Keys.Except(actual.Keys))
            Fail($"expected output not produced: {path}");
        foreach (var path in actual.Keys.Except(expected.Keys))
            Fail($"unexpected output (NT65_UPDATE=1 to accept): {path}");
        foreach (var (path, text) in actual)
        {
            if (expected.TryGetValue(path, out var want) && want != text)
                Fail($"output differs (NT65_UPDATE=1 to accept): {path}\n{FirstDifference(want, text)}");
        }
        return failures;
    }

    private static IEnumerable<(string, IReadOnlyCollection<SourceFile>)> OtherOrders(IReadOnlyList<SourceFile> files)
    {
        if (files.Count < 2)
            yield break;
        yield return ("reversed", [.. files.Reverse()]);
        // A fixed seed keeps a failure reproducible.
        var shuffled = files.ToArray();
        new Random(65).Shuffle(shuffled);
        yield return ("shuffled", shuffled);
    }

    // Path and text are what "identical output" means; the lengths beside them are worked
    // out from the same text.
    private static bool Same(Compilation a, Compilation b) =>
        Written(a).SequenceEqual(Written(b))
        && a.Diagnostics.Select(FixtureCase.Format).Order(StringComparer.Ordinal)
            .SequenceEqual(b.Diagnostics.Select(FixtureCase.Format).Order(StringComparer.Ordinal));

    private static IEnumerable<(string Path, string Text)> Written(Compilation compilation) =>
        compilation.Outputs.OrderBy(o => o.Path, StringComparer.Ordinal).Select(o => (o.Path, o.Text));

    private static string FirstDifference(string expected, string actual)
    {
        var want = expected.Split('\n');
        var got = actual.Split('\n');
        for (var i = 0; i < Math.Max(want.Length, got.Length); i++)
        {
            var w = i < want.Length ? want[i] : "<end of file>";
            var g = i < got.Length ? got[i] : "<end of file>";
            if (w != g)
                return $"  line {i + 1}\n  expected: {w}\n  actual:   {g}";
        }
        return "  (line endings or trailing text differ)";
    }
}
