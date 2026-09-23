using Norristown.Processor;

namespace Norristown.Tests.Oracle;

/// <summary>
/// The corpus and the examples: realistic programs, built, assembled and linked as their own build scripts
/// do. Fixtures test each construct in its own form; these catch the combinations real code
/// uses. They are part of the oracle suite, not the edit loop.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class CorpusTests
{
    [Fact]
    public void EveryProgramBuildsAssemblesToTheComputedLengthsAndLinks()
    {
        var programs = CorpusProgram.All();
        if (Repo.Selection is null)
            Assert.NotEmpty(programs);
        var failures = Repo.CollectFailures(programs, program => Check(program, program.Compile()));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Every macro call the editor offers to inline, in every corpus program, is inlined, and
    /// replacing a call with its expansion must not change what a real program assembles to.
    /// The fixtures check each construct; these are the combinations real code uses.
    /// </summary>
    [Fact]
    public void WritingOutEveryCallLeavesTheProgramsTheSame()
    {
        var programs = CorpusProgram.All();
        if (Repo.Selection is null)
            Assert.NotEmpty(programs);
        var failures = Repo.CollectFailures(programs, program =>
        {
            long? Length(string path)
            {
                var file = Path.Combine(program.Directory, path.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(file) ? new FileInfo(file).Length : null;
            }

            return Fixtures.FixtureRunner.Inlined(
                program.Name, program.Project, program.Sources, Length,
                Compiler.Analyze(
                    [.. program.Sources.Select(Norristown.Syntax.SyntaxTree.Parse)], program.Project, Length));
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// A debug build and a release build of the C64 program differ only in what <c>DEBUG</c>
    /// guards, however the guard is indented: the state check in <c>dispatch</c>
    /// (<c>cpx #</c>, <c>bcc</c>, <c>brk #</c>) and the two border flashes of <c>trace!</c>
    /// (<c>lda #</c>, <c>sta</c> absolute) in <c>main_loop</c>.
    /// </summary>
    [Fact]
    public void TheC64ProgramBuiltWithoutDebugHasNoDebugCode()
    {
        var program = CorpusProgram.All().SingleOrDefault(p => p.Name == "c64");
        if (program is null)
            return;
        var release = program.Compile(new Define("DEBUG", 0, default));
        var failures = Check(program, release).ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        var debug = program.Compile(new Define("DEBUG", 1, default));

        Assert.DoesNotContain("brk", Main(release).Text);
        Assert.Contains("brk", Main(debug).Text);
        Assert.Equal((2 + 2 + 2) + 2 * (2 + 3), Bytes(Main(debug)) - Bytes(Main(release)));

        static OutputFile Main(Compilation compilation) =>
            compilation.Ca65.Single(o => o.Path.EndsWith("/main.s", StringComparison.Ordinal));
        static int Bytes(OutputFile output) => output.LineBytes.Where(bytes => bytes > 0).Sum();
    }

    /// <summary>
    /// The interop program's C is compiled with the pinned cc65 against the header nt65 writes, so
    /// what the header declares is C cc65 accepts, and the struct sizes it asserts are cc65's.
    /// </summary>
    [Fact]
    public void TheInteropCCompilesAgainstTheGeneratedHeader()
    {
        var program = CorpusProgram.All().SingleOrDefault(p => p.Name == "interop");
        if (program is null)
            return;
        var compilation = Compiler.Compile(program.Sources, program.Project, _ => 16, "nt65.h");
        Assert.NotNull(compilation.Header);

        var said = Ca65Oracle.Pinned.CompileC(Path.Combine(program.Directory, "c", "main.c"), [("nt65.h", compilation.Header)]);
        Assert.True(said.Length == 0, $"cc65 reported:\n{said}\nagainst:\n{compilation.Header}");
    }

    private static IEnumerable<string> Check(CorpusProgram program, Compilation compilation)
    {
        if (compilation.Diagnostics.Count > 0)
        {
            yield return $"[{program.Name}] nt65 reported:\n" + string.Join("\n", compilation.Diagnostics.Select(d =>
                $"  {d.Span.File}:{d.Span.Line}: {d.Severity.ToString().ToLowerInvariant()}: {d.Message}"));
            yield break;
        }

        foreach (var output in compilation.Ca65)
        {
            foreach (var problem in Emit.BareNames.Problems(program.Name, output.Path, output.Text))
                yield return problem;
        }

        var failures = Repo.CollectFailures([.. compilation.Ca65], output =>
            OracleTests.AssemblesToComputedLengths(program.Name, output, output.Path, program.Other));
        foreach (var failure in failures)
            yield return failure;
        if (failures.Count > 0)
            yield break;

        var result = Ca65Oracle.Pinned.Link(
            program.LinkerConfig,
            [.. program.HandWritten, .. compilation.Ca65.Select(o => (o.Path, o.Text))],
            program.Other);
        if (!result.Succeeded)
            yield return $"[{program.Name}] ld65 reported:\n{result.Messages}";
        else if (result.Binary.Length == 0)
            yield return $"[{program.Name}] linked, but wrote no bytes";
    }
}
