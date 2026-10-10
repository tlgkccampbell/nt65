namespace Norristown.Tests.Syntax;

/// <summary>
/// Represents one source of the repository as it stands or with one of its lines cut short, which
/// is what a file nobody has finished typing looks like. The sweeps that must hold on broken
/// lines as well as whole ones run over every variant of every source.
/// </summary>
/// <param name="Where">The source's name and the cut, as a failure message names the variant.</param>
/// <param name="Path">The source's name, relative to the repository.</param>
/// <param name="Text">The variant's text.</param>
internal sealed record SourceVariant(string Where, string Path, string Text)
{
    /// <summary>
    /// Returns every variant of every source in the repository, and fails when there are too few
    /// of them to be every source's.
    /// </summary>
    public static List<SourceVariant> All()
    {
        var variants = Repo.Sources()
            .SelectMany(path => BrokenLines.Of(path)
                .Select((text, cut) => new SourceVariant($"{Repo.Named(path)} cut {cut}", Repo.Named(path), text)))
            .ToList();
        Assert.True(variants.Count > 1000, $"{variants.Count} variants is too few to be every source's");
        return variants;
    }
}
