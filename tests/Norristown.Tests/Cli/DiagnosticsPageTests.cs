using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// Tests the page that <c>nt65 explain --markdown</c> writes from the catalog, and the areas
/// the catalog groups its entries under.
/// </summary>
public sealed class DiagnosticsPageTests
{
    /// <summary>The page has a heading for every area and an entry for every diagnostic.</summary>
    [Fact]
    public void ThePageHasEveryAreaAndEveryEntry()
    {
        var (code, written, error) = Nt65.Apart(Directory.GetCurrentDirectory(), false, "explain", "--markdown");

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(error);
        foreach (var area in Catalog.Areas)
            Assert.Contains($"## {area.Name}\n", written, StringComparison.Ordinal);
        foreach (var entry in Catalog.All)
            Assert.Contains($"`{entry.Id}`", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every entry is under exactly one heading, and every heading has entries. Otherwise an
    /// entry added below the last heading of the catalog would be filed under it in silence.
    /// </summary>
    [Fact]
    public void EveryEntryIsUnderOneOfTheAreas()
    {
        Assert.Equal(Catalog.Areas.Count, Catalog.Areas.Distinct().Count());
        foreach (var area in Catalog.Areas)
            Assert.Contains(Catalog.All, entry => entry.Area == area);
        foreach (var entry in Catalog.All)
            Assert.True(Catalog.Areas.Contains(entry.Area), $"`{entry.Id}` is under no heading the page prints");
    }
}
