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

        // Numbered placeholders such as `{0}` mean nothing to a reader, so they are shown as `...`.
        Assert.DoesNotContain("{0}", said, StringComparison.Ordinal);
        Assert.Contains("\"diagnostics\": { \"unused-symbol\": \"off\" }", said, StringComparison.Ordinal);
    }

    /// <summary>Every entry can be explained, whatever its message format contains.</summary>
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

    /// <summary>An unknown name is treated as a mistake, and the closest known name is suggested.</summary>
    [Fact]
    public void ANameItHasNoEntryForSaysWhichOneIsNear()
    {
        var (code, said, problems) = Run("explain", "unused-symbols");

        Assert.Equal(2, code);
        Assert.Empty(said);
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
        var (code, said, _) = Run("explain", "--help");

        Assert.Equal(0, code);
        Assert.Contains("nt65 explain [<diagnostic> | --markdown]", said, StringComparison.Ordinal);
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
