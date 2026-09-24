namespace Norristown.Tests;

/// <summary>Checks the one rule for comparing file paths that the CLI and the language server share.</summary>
public sealed class FilePathsTests
{
    /// <summary>
    /// Case is ignored where the default file system ignores it, on Windows and on macOS, and
    /// the comparer and the comparison agree.
    /// </summary>
    [Fact]
    public void CaseIsIgnoredOnWindowsAndMacOS()
    {
        var ignored = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        Assert.Equal(ignored, FilePaths.IgnoresCase);
        Assert.Equal(ignored, FilePaths.Comparer.Equals("src/Main.nt65", "src/main.nt65"));
        Assert.Equal(ignored, string.Equals("src/Main.nt65", "src/main.nt65", FilePaths.Comparison));
    }
}
