using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Oracle;

/// <summary>
/// Checks that <c>scripts/test.ps1 -Ca65 -Fixture &lt;text&gt;</c> ran something. Every oracle
/// test restricts itself to the fixtures and programs the selection matches, and does nothing
/// when it matches none, so a misspelled selection would otherwise pass with nothing checked —
/// the worst outcome, since it looks exactly like the success the run was asking for.
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
