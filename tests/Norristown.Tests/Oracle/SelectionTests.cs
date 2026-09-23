using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Oracle;

/// <summary>
/// Checks that <c>scripts/test.ps1 -Ca65 -Fixture &lt;text&gt;</c> ran something. Every oracle
/// test restricts itself to the fixtures and programs the selection matches, and is skipped
/// when it matches none. Without this check, a misspelled selection would skip every test,
/// and a run with nothing but skips exits as successfully as one that checked everything.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class SelectionTests
{
    [Fact]
    public void AFilterThatMatchesNothingFails()
    {
        Assert.SkipWhen(Repo.Selection is null, "NT65_FIXTURE is not set, so there is no selection to check");
        var filter = Repo.Selection;
        var fixtures = FixtureCase.All().Count;
        var programs = CorpusProgram.All().Count;
        Assert.True(fixtures + programs > 0,
            $"no fixture and no corpus program has a name containing \"{filter}\", so this run checked nothing");
    }
}
