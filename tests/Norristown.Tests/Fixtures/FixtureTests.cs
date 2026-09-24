namespace Norristown.Tests.Fixtures;

public sealed class FixtureTests
{
    /// <summary>
    /// Runs every fixture in one test, in parallel. Failures from all fixtures are reported
    /// together. Select one fixture with NT65_FIXTURE.
    /// </summary>
    [Fact]
    public void Fixtures()
    {
        var fixtures = FixtureCase.All();
        if (Repo.Selection is { } filter)
            Assert.True(fixtures.Count > 0, $"no fixture name contains \"{filter}\"");
        var failures = Repo.CollectFailures(
            fixtures, f => FixtureRunner.Run(f, FixtureRunner.UpdateMode, FixtureRunner.ThoroughMode));
        Assert.True(failures.Count == 0, $"{failures.Count} fixture failure(s):\n" + string.Join("\n", failures));
    }
}
