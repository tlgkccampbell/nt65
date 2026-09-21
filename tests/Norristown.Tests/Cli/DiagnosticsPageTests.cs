using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// <c>docs/DIAGNOSTICS.md</c> against the catalogue it is written from. The page is a file
/// nothing compiles, so nothing else would notice it drifting from what the code says.
/// </summary>
public sealed class DiagnosticsPageTests
{
    /// <summary>Where the page is kept.</summary>
    private static readonly string Path = Repo.Path("docs", "DIAGNOSTICS.md");

    [Fact]
    public void ThePageIsWhatTheCatalogueWouldWrite()
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(
            ["explain", "--markdown"], Directory.GetCurrentDirectory(), output, error,
            cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        var written = output.ToString();
        if (Fixtures.FixtureRunner.UpdateMode)
        {
            Repo.WriteText(Path, written);
            return;
        }
        Assert.True(
            File.Exists(Path) && Repo.ReadText(Path).ReplaceLineEndings("\n") == written,
            "docs/DIAGNOSTICS.md is out of date; run scripts/test.ps1 -Update");
    }

    /// <summary>
    /// Every entry is under exactly one heading, and every heading has entries: an entry added
    /// below the last heading of the catalogue would otherwise be filed under it in silence.
    /// </summary>
    [Fact]
    public void EveryEntryIsUnderOneOfTheAreas()
    {
        Assert.Equal(Catalogue.Areas.Count, Catalogue.Areas.Distinct().Count());
        foreach (var area in Catalogue.Areas)
            Assert.Contains(Catalogue.All, entry => entry.Area == area);
        foreach (var entry in Catalogue.All)
            Assert.True(Catalogue.Areas.Contains(entry.Area), $"`{entry.Id}` is under no heading the page prints");
    }
}
