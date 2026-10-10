using Norristown.Project;

namespace Norristown.Tests.Project;

/// <summary>
/// Checks that a project's <c>files</c> globs follow the file system's rule for case, so the
/// editor's answer to whether a file is part of a project agrees with what a build finds.
/// </summary>
public sealed class SourceGlobsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "nt65-source-globs");

    [Theory]
    [InlineData("src/*.nt65", "src/main.nt65")]
    [InlineData("src/**/*.nt65", "src/gfx/sprites.nt65")]
    [InlineData("*.nt65", "main.nt65")]
    public void APathSpelledAsTheGlobMatchesWhetherOrNotCaseCounts(string glob, string path)
    {
        Assert.True(SourceGlobs.Matches(Root, glob, path, ignoresCase: true));
        Assert.True(SourceGlobs.Matches(Root, glob, path, ignoresCase: false));
    }

    /// <summary>
    /// A path that differs from the glob only in case is the same file where the file system
    /// ignores case, in its directories and in its name alike, and another file where it does not.
    /// </summary>
    [Theory]
    [InlineData("src/*.nt65", "SRC/main.nt65")]
    [InlineData("src/*.nt65", "src/MAIN.NT65")]
    [InlineData("src/**/*.nt65", "Src/gfx/Sprites.NT65")]
    public void APathDifferingOnlyInCaseMatchesOnlyWhereCaseIsIgnored(string glob, string path)
    {
        Assert.True(SourceGlobs.Matches(Root, glob, path, ignoresCase: true));
        Assert.False(SourceGlobs.Matches(Root, glob, path, ignoresCase: false));
    }

    /// <summary>
    /// The rule a glob follows is the file system's, the one every path comparison in nt65 uses,
    /// rather than a rule of its own.
    /// </summary>
    [Fact]
    public void AGlobFollowsTheFileSystemsRuleForCase()
    {
        Assert.Equal(FilePaths.IgnoresCase, SourceGlobs.Matches(Root, "src/*.nt65", "SRC/MAIN.NT65"));
    }
}
