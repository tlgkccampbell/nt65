using Norristown.Syntax;

namespace Norristown.Tests.Fixtures;

internal static class FixtureRunner
{
    /// <summary>True when NT65_UPDATE is set: expected output is rewritten instead of compared.</summary>
    public static bool UpdateMode => Environment.GetEnvironmentVariable("NT65_UPDATE") is "1" or "true";

    /// <summary>Runs one fixture and returns its failures, empty when it passes.</summary>
    public static IEnumerable<string> Run(FixtureCase fixture, bool update = false)
    {
        // What the analysis meant is kept from the first of the runs, so that what the editor
        // would show beside each source can be checked against what the build wrote without
        // reading the program a second time.
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
            update).ToList();
        if (analyzed is { } program)
        {
            failures.AddRange(Shown(fixture, program));
            failures.AddRange(Inlined(fixture, program));
        }
        return failures;
    }

    /// <summary>
    /// Every macro call the editor offers to write out, written out: the file has to build to
    /// the same bytes with the call written out as it did with the call, and nothing new may be
    /// wrong with it. A macro whose only call was written out is one nothing names any more,
    /// which is the warning it should be and not a difference.
    /// </summary>
    private static IEnumerable<string> Inlined(FixtureCase fixture, ProgramAnalysis analysis)
    {
        if (analysis.Diagnostics.Any(d => d.Severity == Severity.Error))
            yield break;
        var was = Reported(analysis);
        foreach (var source in fixture.Sources)
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

                // The one file is read again and the rest of the program is kept, which is the
                // path an editor takes, and only that file is written out again: the edit is
                // inside one routine, and no other file can have moved.
                var written = source.Text[..edit.Span.Start] + edit.Text + source.Text[edit.Span.End..];
                var after = Compiler.Analyze(
                    [.. analysis.Program.Files.Select(file => file.Tree)
                        .Where(tree => tree != analysis.Defines)
                        .Select(tree => tree.Path == source.Path
                            ? SyntaxTree.Parse(tree.Path, written)
                            : tree)],
                    fixture.Project, fixture.BinaryLength, analysis);
                var where = $"[{fixture.Name}] writing out the call on "
                    + $"{source.Path}:{model.Tree.GetLineIndex(call.Position) + 1}";
                if (!Reported(after).SequenceEqual(was))
                    yield return $"{where} reports {string.Join("; ", Reported(after).Except(was))}";
                else if (Assembled(analysis, fixture, source.Path) != Assembled(after, fixture, source.Path))
                    yield return $"{where} changes what it assembles to";
            }
        }
    }

    /// <summary>What is wrong with a program, leaving out what a call written out makes unused.</summary>
    private static IReadOnlyList<string> Reported(ProgramAnalysis analysis) =>
        [.. analysis.Diagnostics.Where(d => d.Id != "unused-symbol").Select(FixtureCase.Format)];

    /// <summary>
    /// What one file assembles to: the length of every line of its output that generates bytes,
    /// in order. A comment and a label generate none, so this is the program and not the ca65
    /// that spells it, and a label an expansion no longer names is not a difference.
    /// </summary>
    private static string Assembled(ProgramAnalysis analysis, FixtureCase fixture, string path) =>
        Compiler.EmitFile(analysis, fixture.Project, path) is { } output
            ? string.Join(",", output.LineBytes.Where(bytes => bytes != 0))
            : "nothing";

    /// <summary>Whether a call is written inside a macro body, where its arguments are not known.</summary>
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
    /// What the editor shows beside each source, against what the build wrote for it: they are
    /// one text, or the view is showing something that is not the program.
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

        // Nothing the output defines at the margin may be a word ca65 reads as an instruction.
        // It is checked here, on what this fixture has already been compiled to, rather than
        // by a pass of its own over every fixture.
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
    /// What a fixture expects against what it got. They are matched on where a diagnostic is
    /// and what it is called; the words are a second expectation, checked once a pair has been
    /// made, so that a reworded message is one line to change rather than a diagnostic gone and
    /// another arrived in its place.
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
