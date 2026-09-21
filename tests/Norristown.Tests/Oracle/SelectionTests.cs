using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Oracle;

/// <summary>
/// That <c>scripts/test.ps1 -Ca65 -Fixture &lt;text&gt;</c> ran something. Every oracle test
/// narrows itself to what the selection names and stands down when it names nothing of its
/// own, so a name with a typo in it used to pass green over an empty set — the worst answer
/// there is, since it looks like the one the run was asking for.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class SelectionTests
{
    [Fact]
    public void AFilterThatMatchesNothingFails()
    {
        if (Repo.Selection is not { } filter)
            return;
        var fixtures = FixtureCase.All().Count;
        var programs = CorpusProgram.All().Count;
        Assert.True(fixtures + programs > 0,
            $"no fixture and no corpus program has a name containing \"{filter}\", so this run checked nothing");
    }
}
