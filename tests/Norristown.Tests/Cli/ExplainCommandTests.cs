using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// <c>nt65 explain</c>: what a diagnostic is about, for someone who has its name from the
/// brackets after a message and wants the paragraph the line had no room for.
/// </summary>
public sealed class ExplainCommandTests
{
    [Fact]
    public void ANameIsExplained()
    {
        var (code, said, problems) = Run("explain", "unused-symbol");

        Assert.Equal(0, code);
        Assert.Empty(problems);
        Assert.StartsWith("unused-symbol, a warning by default", said, StringComparison.Ordinal);
        Assert.Contains(Catalogue.Find("unused-symbol")!.Explanation.Split(' ')[0], said, StringComparison.Ordinal);

        // The holes a sentence leaves are numbered for nt65, and are marked rather than shown.
        Assert.DoesNotContain("{0}", said, StringComparison.Ordinal);
        Assert.Contains("\"diagnostics\": { \"unused-symbol\": \"off\" }", said, StringComparison.Ordinal);
    }

    /// <summary>Every entry can be asked about, whatever its sentence is made of.</summary>
    [Fact]
    public void EveryNameIsExplained()
    {
        foreach (var descriptor in Catalogue.All)
        {
            var (code, said, _) = Run("explain", descriptor.Id);
            Assert.Equal(0, code);
            Assert.StartsWith(descriptor.Id + ",", said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NamedNothingItListsThem()
    {
        var (code, said, problems) = Run("explain");

        Assert.Equal(0, code);
        Assert.Empty(problems);
        foreach (var descriptor in Catalogue.All)
            Assert.Contains(descriptor.Id, said, StringComparison.Ordinal);
    }

    /// <summary>A name nt65 has no entry for is a mistake, and is answered with the one it is nearly.</summary>
    [Fact]
    public void ANameItHasNoEntryForSaysWhichOneIsNear()
    {
        var (code, said, problems) = Run("explain", "unused-symbols");

        Assert.Equal(2, code);
        Assert.Empty(said);
        Assert.Contains("`unused-symbols` is not a diagnostic nt65 reports; `unused-symbol` is", problems, StringComparison.Ordinal);
        Assert.Contains("`nt65 explain` lists them", problems, StringComparison.Ordinal);
    }

    [Fact]
    public void ItTakesOneName()
    {
        var (code, _, problems) = Run("explain", "unused-symbol", "mnemonic-name");

        Assert.Equal(2, code);
        Assert.Contains("explain takes one diagnostic's name", problems, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpIsTheUsageText()
    {
        var (code, said, _) = Run("explain", "--help");

        Assert.Equal(0, code);
        Assert.Contains("nt65 explain [<diagnostic>]", said, StringComparison.Ordinal);
    }

    private static (int Code, string Said, string Problems) Run(params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(
            arguments, Directory.GetCurrentDirectory(), output, error,
            cancellation: TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }
}
