using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Oracle;

/// <summary>
/// Represents the hand-written half of a fixture that links: the linker configuration and the
/// ca65 modules in its <c>link</c> directory, which nt65's output must link against.
/// </summary>
/// <param name="Config">The text of the fixture's <c>link/link.cfg</c>.</param>
/// <param name="Modules">The hand-written ca65 modules, each named by its file name.</param>
internal sealed record FixtureLink(string Config, IReadOnlyList<(string Name, string Source)> Modules)
{
    /// <summary>
    /// Returns the link files of <paramref name="fixture"/>, or null when it has no linker
    /// configuration and so cannot link.
    /// </summary>
    public static FixtureLink? Of(FixtureCase fixture)
    {
        var directory = Path.Combine(fixture.Directory, "link");
        var config = Path.Combine(directory, "link.cfg");
        if (!File.Exists(config))
            return null;
        return new FixtureLink(
            Repo.ReadText(config),
            [.. Directory.GetFiles(directory, "*.s")
                .Order(StringComparer.Ordinal)
                .Select(path => (Path.GetFileName(path), Repo.ReadText(path)))]);
    }
}
