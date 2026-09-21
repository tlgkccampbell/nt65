using System.Text.RegularExpressions;

namespace Norristown.Tests;

/// <summary>
/// What the catalogue promises about itself. The names are a compatibility promise, so they are
/// held to one spelling; the sentences are composite format strings, so a brace in one that is
/// not a hole would throw where it is said rather than here.
/// </summary>
public sealed partial class CatalogueTests
{
    [Fact]
    public void EveryNameIsKebabCaseAndDeclaredOnce()
    {
        Assert.NotEmpty(Catalogue.All);
        foreach (var descriptor in Catalogue.All)
            Assert.Matches(Name(), descriptor.Id);
        Assert.Equal(Catalogue.All.Count, Catalogue.All.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A name is what <c>nt65 explain</c> and the project file are given, so both find it.</summary>
    [Fact]
    public void EveryNameIsFound()
    {
        foreach (var descriptor in Catalogue.All)
            Assert.Same(descriptor, Catalogue.Find(descriptor.Id));
        Assert.Null(Catalogue.Find("no-such-diagnostic"));
    }

    /// <summary>
    /// Every sentence is one a site can say: each brace is a hole or an escape, and the holes
    /// run from the first with none left out, so no argument a site passes goes nowhere.
    /// </summary>
    [Fact]
    public void EverySentenceCanBeSaid()
    {
        foreach (var descriptor in Catalogue.All)
        {
            var holes = Holes(descriptor.Format);
            object?[] arguments = [.. Enumerable.Range(0, holes).Select(object? (i) => $"<{i}>")];
            var said = descriptor.Says(arguments).Text;
            for (var i = 0; i < holes; i++)
                Assert.Contains($"<{i}>", said, StringComparison.Ordinal);
        }
    }

    /// <summary>Every entry says something the message does not.</summary>
    [Fact]
    public void EveryEntryIsExplained()
    {
        foreach (var descriptor in Catalogue.All)
        {
            Assert.True(descriptor.Explanation.Length > 40, descriptor.Id);
            Assert.EndsWith(".", descriptor.Explanation, StringComparison.Ordinal);
            Assert.NotEqual(descriptor.Format, descriptor.Explanation);
        }
    }

    /// <summary>How many holes a format has, which is one more than the highest it numbers.</summary>
    private static int Holes(string format) =>
        Hole().Matches(format).Select(m => int.Parse(m.Groups[1].Value) + 1).DefaultIfEmpty(0).Max();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Name();

    [GeneratedRegex(@"(?<!\{)\{(\d+)\}")]
    private static partial Regex Hole();
}
