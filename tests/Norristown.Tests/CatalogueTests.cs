using System.Text.RegularExpressions;

namespace Norristown.Tests;

/// <summary>
/// Checks the catalogue's own invariants. The names are a compatibility promise, so they must
/// all follow one naming style. The messages are composite format strings, so a brace in one that
/// is not a placeholder would otherwise throw only when the diagnostic is reported, not here.
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

    /// <summary>
    /// A name is what <c>nt65 explain</c> and the project file are given, so looking it up must
    /// find its entry.
    /// </summary>
    [Fact]
    public void EveryNameIsFound()
    {
        foreach (var descriptor in Catalogue.All)
            Assert.Same(descriptor, Catalogue.Find(descriptor.Id));
        Assert.Null(Catalogue.Find("no-such-diagnostic"));
    }

    /// <summary>
    /// Every message format can be formatted. Each brace is a placeholder or an escaped brace,
    /// and the placeholders are numbered from <c>{0}</c> with none skipped, so every argument a
    /// reporting site passes appears in the message.
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

    /// <summary>Every entry's explanation says something the message does not.</summary>
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

    /// <summary>
    /// Returns the number of placeholders a format has, taken as one more than the highest
    /// placeholder number.
    /// </summary>
    private static int Holes(string format) =>
        Hole().Matches(format).Select(m => int.Parse(m.Groups[1].Value) + 1).DefaultIfEmpty(0).Max();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Name();

    [GeneratedRegex(@"(?<!\{)\{(\d+)\}")]
    private static partial Regex Hole();
}
