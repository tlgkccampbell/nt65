using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// The page <c>nt65 explain --markdown</c> writes from the catalogue, and the areas the catalogue
/// is written under.
/// </summary>
public sealed class DiagnosticsPageTests
{
    /// <summary>The page has a heading for every area and an entry for every diagnostic.</summary>
    [Fact]
    public void ThePageHasEveryAreaAndEveryEntry()
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(
            ["explain", "--markdown"], Directory.GetCurrentDirectory(), output, error,
            cancellation: TestTimeout.Token());

        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        var written = output.ToString();
        foreach (var area in Catalogue.Areas)
            Assert.Contains($"## {area.Name}\n", written, StringComparison.Ordinal);
        foreach (var entry in Catalogue.All)
            Assert.Contains($"`{entry.Id}`", written, StringComparison.Ordinal);
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
