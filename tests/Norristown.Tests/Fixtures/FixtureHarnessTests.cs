namespace Norristown.Tests.Fixtures;

/// <summary>The harness itself, run against a fake compiler so it can be checked before the real one does anything.</summary>
public sealed class FixtureHarnessTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("nt65-fixture-");

    public void Dispose() => dir.Delete(recursive: true);

    [Fact]
    public void InlineDiagnosticsAreParsedWithFileAndLine()
    {
        var file = new SourceFile("a.nt65", "lda #1\r\n  brx ;! error: unknown mnemonic\n;!warning:  spaced out  \n");
        Assert.Equal(
            ["a.nt65:2: error: unknown mnemonic", "a.nt65:3: warning: spaced out"],
            FixtureCase.ParseInlineDiagnostics(file));
    }

    [Fact]
    public void MatchingDiagnosticsAndOutputPass()
    {
        var fixture = Write(("main.nt65", "brx ;! error: unknown mnemonic\n"), ("expected/main.s", "brx\n"));
        var compile = Returns([new OutputFile("main.s", "brx\n")], Error("main.nt65", 1, "unknown mnemonic"));
        Assert.Empty(FixtureRunner.Run(fixture, compile));
    }

    [Fact]
    public void MissingAndUnexpectedDiagnosticsFail()
    {
        var fixture = Write(("main.nt65", "brx ;! error: unknown mnemonic\n"));
        var failures = FixtureRunner.Run(fixture, Returns([], Error("main.nt65", 1, "something else"))).ToList();
        Assert.Equal(2, failures.Count);
        Assert.Contains("expected diagnostic not reported: main.nt65:1: error: unknown mnemonic", failures[0]);
        Assert.Contains("unexpected diagnostic: main.nt65:1: error: something else", failures[1]);
    }

    [Fact]
    public void OutputDifferencesFailAndUpdateRewritesThem()
    {
        var fixture = Write(("main.nt65", ""), ("expected/main.s", "old\n"), ("expected/gone.s", "x\n"));
        var compile = Returns([new OutputFile("main.s", "new\n"), new OutputFile("more.s", "y\n")]);

        var failures = FixtureRunner.Run(fixture, compile).ToList();
        Assert.Contains(failures, f => f.Contains("expected output not produced: gone.s"));
        Assert.Contains(failures, f => f.Contains("unexpected output (NT65_UPDATE=1 to accept): more.s"));
        Assert.Contains(failures, f => f.Contains("output differs") && f.Contains("expected: old"));

        Assert.Empty(FixtureRunner.Run(fixture, compile, update: true));
        Assert.Empty(FixtureRunner.Run(fixture, compile));
    }

    [Fact]
    public void OrderDependenceFails()
    {
        var fixture = Write(("a.nt65", ""), ("b.nt65", ""));
        Compilation FirstFileWins(IReadOnlyCollection<SourceFile> files) =>
            new([new OutputFile("out.s", files.First().Path)], []);
        Assert.Contains(FixtureRunner.Run(fixture, FirstFileWins, update: true),
            f => f.Contains("change when the files are reversed"));
    }

    private static Func<IReadOnlyCollection<SourceFile>, Compilation> Returns(
        IReadOnlyList<OutputFile> outputs, params Diagnostic[] diagnostics) => _ => new(outputs, diagnostics);

    private static Diagnostic Error(string file, int line, string message) =>
        new(new Span(file, line, 1, 2), Severity.Error, message);

    private FixtureCase Write(params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
            Repo.WriteText(Path.Combine(dir.FullName, path), text);
        return FixtureCase.Load(dir.FullName).Single();
    }
}
