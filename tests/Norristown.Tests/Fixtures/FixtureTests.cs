namespace Norristown.Tests.Fixtures;

public sealed class FixtureTests
{
    /// <summary>
    /// Every fixture in one test, run in parallel. Failures from all fixtures are reported
    /// together; select one with NT65_FIXTURE.
    /// </summary>
    [Fact]
    public void Fixtures()
    {
        var fixtures = FixtureCase.All();
        if (Environment.GetEnvironmentVariable("NT65_FIXTURE") is { Length: > 0 } filter)
            Assert.True(fixtures.Count > 0, $"no fixture name contains \"{filter}\"");
        var failures = Repo.CollectFailures(fixtures, f => FixtureRunner.Run(f, FixtureRunner.UpdateMode));
        Assert.True(failures.Count == 0, $"{failures.Count} fixture failure(s):\n" + string.Join("\n", failures));
    }
}
