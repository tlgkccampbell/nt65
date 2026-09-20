using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Norristown.SyntaxGenerator;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The generator as the compiler runs it: over the repository's table it writes one file per type
/// and says nothing, and over a table it cannot read it says so where the trouble is instead of
/// throwing out of the build.
/// </summary>
public sealed class GeneratorDriverTests
{
    [Fact]
    public void TheTableMakesAFilePerTypeAndNoDiagnostic()
    {
        var table = Repo.ReadText(Repo.Path(NodeTable.File.Split('/')));
        var run = Run(table);
        Assert.Empty(run.Diagnostics);
        Assert.Equal(
            SyntaxWriter.Files(NodeTable.Read(table)).Keys,
            run.GeneratedSources.Select(source => source.HintName).Order(StringComparer.Ordinal));
        Assert.Contains("Nodes/ProcDeclarationSyntax.g.cs", run.GeneratedSources.Select(source => source.HintName));
        Assert.Contains("InternalSyntax/ProcDeclarationSyntax.g.cs", run.GeneratedSources.Select(source => source.HintName));
    }

    [Fact]
    public void ATableTheReaderRefusesIsADiagnosticWithItsLine()
    {
        var run = Run("<Tree>\n  <Node Name=\"WidgetSyntax\"/>\n</Tree>\n");
        var diagnostic = Assert.Single(run.Diagnostics);
        Assert.Equal("NT1001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("wants a Base", diagnostic.GetMessage());
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void TableTextThatIsNotXmlIsADiagnosticToo()
    {
        var run = Run("<Tree>\n  <Node Name=\"WidgetSyntax\">\n</Tree>\n");
        var diagnostic = Assert.Single(run.Diagnostics);
        Assert.Equal("NT1001", diagnostic.Id);
        Assert.Equal(Repo.Path("src", "Norristown.Core", "Syntax", "Syntax.xml"), diagnostic.Location.GetLineSpan().Path);
        Assert.Empty(run.GeneratedSources);
    }

    /// <summary>
    /// An edit to the project that is not an edit to the table writes nothing again: the pipeline
    /// hangs off the table's text, which is what keeps an editor from regenerating a hundred
    /// classes on every keystroke.
    /// </summary>
    [Fact]
    public void AnEditThatIsNotToTheTableRegeneratesNothing()
    {
        var table = new Table(Repo.Path(NodeTable.File.Split('/')), Repo.ReadText(Repo.Path(NodeTable.File.Split('/'))));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SyntaxSourceGenerator().AsSourceGenerator()],
            additionalTexts: [table],
            parseOptions: null,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        var token = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create("Norristown.Core.Table");
        driver = driver.RunGenerators(compilation, token);
        var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated;", cancellationToken: token));
        var again = driver.RunGenerators(edited, token);

        var outputs = again.GetRunResult().Results.Single().TrackedOutputSteps
            .SelectMany(step => step.Value)
            .SelectMany(step => step.Outputs)
            .ToList();
        Assert.NotEmpty(outputs);
        Assert.All(outputs, output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }

    private static GeneratorRunResult Run(string table)
    {
        var driver = CSharpGeneratorDriver
            .Create(new SyntaxSourceGenerator())
            .AddAdditionalTexts([new Table(Repo.Path(NodeTable.File.Split('/')), table)]);
        var compilation = CSharpCompilation.Create("Norristown.Core.Table");
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private sealed class Table(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
