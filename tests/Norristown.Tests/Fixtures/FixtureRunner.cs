using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.Fixtures;

/// <summary>Runs fixtures and reports how each differs from what it expects.</summary>
internal static class FixtureRunner
{
    /// <summary>
    /// Gets a value indicating whether NT65_UPDATE is set, in which case expected output is
    /// rewritten instead of compared.
    /// </summary>
    public static bool UpdateMode => Environment.GetEnvironmentVariable("NT65_UPDATE") is "1" or "true";

    /// <summary>
    /// Gets a value indicating whether NT65_THOROUGH is set, as <c>scripts/test.ps1 -Thorough</c>
    /// and the gate do. In that mode every fixture is also compiled with its files reversed and
    /// shuffled, not only in source order. The edit loop leaves it off, because the two extra
    /// runs cost a compile each and test the set of files rather than the edit just made.
    /// </summary>
    public static bool ThoroughMode => Environment.GetEnvironmentVariable("NT65_THOROUGH") is "1" or "true";

    /// <summary>Runs one fixture and returns its failures, empty when it passes.</summary>
    public static IEnumerable<string> Run(FixtureCase fixture, bool update = false, bool thorough = false)
    {
        // The analysis from the first run is kept. The output preview of each source can then be
        // checked against what the build wrote, and the macro calls inlined, without analysing
        // the program again.
        ProgramAnalysis? analyzed = null;
        var failures = Run(
            fixture,
            sources =>
            {
                var analysis = Compiler.Analyze(
                    [.. sources.Select(SyntaxTree.Parse)], fixture.Project, fixture.BinaryLength);
                analyzed ??= analysis;
                return Compiler.Emit(analysis, fixture.Project);
            },
            update,
            thorough).ToList();
        if (analyzed is { } program)
        {
            failures.AddRange(Shown(fixture, program));
            failures.AddRange(Inlined(
                fixture.Name, fixture.Project, fixture.Sources, fixture.BinaryLength, program));
        }
        return failures;
    }

    public static IEnumerable<string> Run(
        FixtureCase fixture, Func<IReadOnlyCollection<SourceFile>, Compilation> compile, bool update = false,
        bool thorough = false)
    {
        var failures = new List<string>();
        void Fail(string message) => failures.Add($"[{fixture.Name}] {message}");

        var compilation = compile(fixture.Sources);

        // Every fixture is checked for full fidelity: the tree and each statement reproduce the
        // source text.
        foreach (var file in fixture.Sources)
        {
            foreach (var problem in Syntax.Fidelity.Problems(SyntaxTree.Parse(file)))
                Fail($"{file.Path}: {problem}");
        }

        // Output is deterministic, so the same sources give byte-identical output. A program is
        // a set of files, so other orders must give identical results too. Other orders are
        // tried only in a thorough run, but every run compiles the same files a second time.
        if (!Same(compilation, compile(fixture.Sources)))
            Fail("output or diagnostics change between two runs over the same files");
        foreach (var (label, order) in OtherOrders(fixture.Sources, thorough))
        {
            if (!Same(compilation, compile(order)))
                Fail($"output or diagnostics change when the files are {label}");
        }

        // No name the output defines at the start of a line may be a word ca65 reads as an
        // instruction. It is checked here, on this fixture's existing compilation, rather than
        // by a separate pass that compiles every fixture again.
        foreach (var output in compilation.Outputs)
            failures.AddRange(Emit.BareNames.Problems(fixture.Name, output.Path, output.Text));

        var actualDiagnostics = compilation.Diagnostics.Select(FixtureCase.Of).ToList();
        var expectedDiagnostics = update
            ? fixture.UpdateInlineDiagnostics(actualDiagnostics)
            : fixture.ExpectedDiagnostics();
        CompareDiagnostics(expectedDiagnostics, actualDiagnostics, Fail);

        var expected = fixture.ExpectedOutputs();
        var actual = compilation.Outputs.ToDictionary(o => o.Path, o => o.Text, StringComparer.Ordinal);
        var expectedDir = Path.Combine(fixture.Directory, fixture.ExpectedDirectory);

        if (update)
        {
            foreach (var path in expected.Keys.Except(actual.Keys))
            {
                File.Delete(Path.Combine(expectedDir, path));
                for (var directory = Path.GetDirectoryName(Path.Combine(expectedDir, path));
                    directory is not null && directory.Length > expectedDir.Length
                        && !System.IO.Directory.EnumerateFileSystemEntries(directory).Any();
                    directory = Path.GetDirectoryName(directory))
                {
                    System.IO.Directory.Delete(directory);
                }
            }
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

    /// <summary>
    /// Inlines, one at a time, every macro call the editor offers to inline, and reports each call
    /// whose inlining changes the program. With the call replaced by its expansion, the file has
    /// to assemble to the same bytes and report no new diagnostics. A macro whose only call was
    /// inlined is no longer used, and the warning that reports this is expected, not a difference.
    /// </summary>
    /// <param name="name">The program's name, as failures report it.</param>
    /// <param name="project">The project settings the program is built with.</param>
    /// <param name="sources">The program's sources, as the analysis read them.</param>
    /// <param name="binaryLength">The length of a file an <c>.incbin</c> names.</param>
    /// <param name="analysis">The program, already analysed.</param>
    public static IEnumerable<string> Inlined(
        string name, ProjectSettings project, IReadOnlyList<SourceFile> sources,
        Func<string, long?> binaryLength, ProgramAnalysis analysis)
    {
        if (analysis.Diagnostics.Any(d => d.Severity == Severity.Error))
            yield break;
        var was = Reported(analysis);
        foreach (var source in sources)
        {
            if (analysis.ModelFor(source.Path) is not { } model)
                continue;
            foreach (var call in model.Tree.Root.DescendantNodes().OfType<MacroCallSyntax>())
            {
                if (InABody(call)
                    || Norristown.LanguageServer.InlineMacro.In(analysis, model, call.Position).ToList()
                        is not [{ Refused: null, Edits: [var edit] }])
                {
                    continue;
                }

                // As in an editor, only the edited file is parsed again and the rest of the program
                // is reused. Only that file is emitted again, because the edit is inside one
                // routine, so no other file's output can have changed.
                var edited = source.Text[..edit.Span.Start] + edit.Text + source.Text[edit.Span.End..];
                var after = Compiler.Analyze(
                    [.. analysis.Program.Files.Select(file => file.Tree)
                        .Where(tree => tree != analysis.Defines)
                        .Select(tree => tree.Path == source.Path
                            ? SyntaxTree.Parse(tree.Path, edited)
                            : tree)],
                    project, binaryLength, analysis);
                var where = $"[{name}] writing out the call on "
                    + $"{source.Path}:{model.Tree.GetLineIndex(call.Position) + 1}";
                if (!Reported(after).SequenceEqual(was))
                    yield return $"{where} reports {string.Join("; ", Reported(after).Except(was))}";
                else if (Assembled(analysis, project, source.Path) != Assembled(after, project, source.Path))
                    yield return $"{where} changes what it assembles to";
            }
        }
    }

    /// <summary>
    /// Returns a program's diagnostics, leaving out the unused-symbol warnings that inlining a
    /// call can cause.
    /// </summary>
    private static IReadOnlyList<string> Reported(ProgramAnalysis analysis) =>
        [.. analysis.Diagnostics.Where(d => d.Id != "unused-symbol").Select(FixtureCase.Format)];

    /// <summary>
    /// Returns what one file assembles to, as the byte count of every output line that generates
    /// bytes, in order. Comments and labels generate no bytes, so this compares the program rather
    /// than the ca65 text that expresses it. A label the expansion no longer uses is therefore
    /// not a difference.
    /// </summary>
    private static string Assembled(ProgramAnalysis analysis, ProjectSettings project, string path) =>
        Compiler.EmitFile(analysis, project, path) is { } output
            ? string.Join(",", output.LineBytes.Where(bytes => bytes != 0))
            : "nothing";

    /// <summary>
    /// Returns whether a call is inside a macro body, where its arguments are not known.
    /// </summary>
    private static bool InABody(SyntaxNode call)
    {
        for (var at = call.Parent; at is not null; at = at.Parent)
        {
            if (at is BlockSyntax { BlockKind: BlockKind.Macro })
                return true;
        }
        return false;
    }

    /// <summary>
    /// Compares the output preview of each source with what the build wrote for it, and reports
    /// each difference. They must be the same text, or the preview is showing something other
    /// than the program.
    /// </summary>
    private static IEnumerable<string> Shown(FixtureCase fixture, ProgramAnalysis analysis)
    {
        if (analysis.Diagnostics.Any(d => d.Severity == Severity.Error))
            yield break;
        foreach (var output in Compiler.Emit(analysis, fixture.Project).Outputs)
        {
            if (output.Kind != OutputKind.Ca65)
                continue;
            var preview = Norristown.Emit.OutputPreview.Of(analysis, fixture.Project, output.Source);
            if (preview is null)
                yield return $"[{fixture.Name}] nothing is shown beside {output.Source}";
            else if (preview.Text != output.Text || !preview.SourceLines.SequenceEqual(output.LineSources))
                yield return $"[{fixture.Name}] what is shown beside {output.Source} is not what was written";
        }
    }

    /// <summary>
    /// Compares the diagnostics a fixture expects with those reported. They are paired on
    /// location, severity and name. The message is a second expectation, checked once a pair has
    /// been made, so that a reworded message is one line to change rather than one diagnostic
    /// missing and another unexpected.
    /// </summary>
    private static void CompareDiagnostics(
        IReadOnlyList<FixtureCase.Expectation> expected,
        IReadOnlyList<FixtureCase.Expectation> actual,
        Action<string> fail)
    {
        var found = actual
            .GroupBy(d => d.Where, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Queue<FixtureCase.Expectation>(group.OrderBy(d => d.Message, StringComparer.Ordinal)),
                StringComparer.Ordinal);

        var wanted = expected
            .OrderBy(d => d.Where, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal);
        foreach (var want in wanted)
        {
            if (!found.TryGetValue(want.Where, out var here) || here.Count == 0)
            {
                fail($"expected diagnostic not reported: {want}");
                continue;
            }
            var got = here.Dequeue();
            if (got.Message != want.Message)
                fail($"message differs (NT65_UPDATE=1 to accept): {want.Where}\n  expected: {want.Message}\n  actual:   {got.Message}");
        }
        foreach (var extra in found.Values.SelectMany(here => here))
            fail($"unexpected diagnostic: {extra}");
    }

    private static IEnumerable<(string, IReadOnlyCollection<SourceFile>)> OtherOrders(
        IReadOnlyList<SourceFile> files, bool thorough)
    {
        if (!thorough || files.Count < 2)
            yield break;
        yield return ("reversed", [.. files.Reverse()]);
        // A fixed seed keeps a failure reproducible.
        var shuffled = files.ToArray();
        new Random(65).Shuffle(shuffled);
        yield return ("shuffled", shuffled);
    }

    // Identical output means identical paths and text. The line byte counts kept with each
    // output are derived from the same text, so they need no separate comparison.
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
