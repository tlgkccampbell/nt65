using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// Tests <c>nt65 explain</c>, which describes what a diagnostic is about. It is for someone who
/// has the diagnostic's name from the brackets after a message and wants the paragraph the line
/// had no room for.
/// </summary>
public sealed class ExplainCommandTests
{
    [Fact]
    public void ANameIsExplained()
    {
        var (code, output, problems) = Run("explain", "unused-symbol");

        Assert.Equal(0, code);
        Assert.Empty(problems);
        Assert.StartsWith("unused-symbol, a warning by default", output, StringComparison.Ordinal);
        Assert.Contains(Catalogue.Find("unused-symbol")!.Explanation.Split(' ')[0], output, StringComparison.Ordinal);

        // Numbered placeholders such as `{0}` mean nothing to a reader, so they are shown as `...`.
        Assert.DoesNotContain("{0}", output, StringComparison.Ordinal);
        Assert.Contains("\"diagnostics\": { \"unused-symbol\": \"off\" }", output, StringComparison.Ordinal);
    }

    /// <summary>Every entry can be explained, no matter what its message format contains.</summary>
    [Fact]
    public void EveryNameIsExplained()
    {
        foreach (var descriptor in Catalogue.All)
        {
            var (code, output, _) = Run("explain", descriptor.Id);
            Assert.Equal(0, code);
            Assert.StartsWith(descriptor.Id + ",", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NamedNothingItListsThem()
    {
        var (code, output, problems) = Run("explain");

        Assert.Equal(0, code);
        Assert.Empty(problems);
        foreach (var descriptor in Catalogue.All)
            Assert.Contains(descriptor.Id, output, StringComparison.Ordinal);
    }

    /// <summary>An unknown name is treated as a mistake, and the closest known name is suggested.</summary>
    [Fact]
    public void ANameWithNoEntryIsReportedWithTheNearestOne()
    {
        var (code, output, problems) = Run("explain", "unused-symbols");

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("no diagnostic is named `unused-symbols`; did you mean `unused-symbol`?", problems, StringComparison.Ordinal);
        Assert.Contains("`nt65 explain` with no name lists every diagnostic", problems, StringComparison.Ordinal);
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
        var (code, output, _) = Run("explain", "--help");

        Assert.Equal(0, code);
        Assert.Contains("nt65 explain [<diagnostic> | --markdown]", output, StringComparison.Ordinal);
    }

    private static (int Code, string Output, string Problems) Run(params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(
            arguments, Directory.GetCurrentDirectory(), output, error,
            cancellation: TestTimeout.Token());
        return (code, output.ToString(), error.ToString());
    }
}
