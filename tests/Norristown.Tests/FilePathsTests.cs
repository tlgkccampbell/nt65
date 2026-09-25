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

    /// <summary>
    /// Where case is ignored, a path is spelled as the file system stores it, up to the first part
    /// that does not exist. The rest is kept as it was given.
    /// </summary>
    [Fact]
    public void APathIsSpelledAsTheFileSystemStoresIt()
    {
        Assert.SkipUnless(FilePaths.IgnoresCase, "only a file system that ignores case has two spellings of one file");
        using var root = new TempFolder("nt65-paths-");
        root.Write("Src/Main.nt65", "");
        var given = Path.Combine(root.FullName, "src", "MAIN.nt65");
        var missing = Path.Combine(root.FullName, "SRC", "Gone", "x.nt65");

        var folder = FilePaths.AsStored(root.FullName);
        Assert.Equal(Path.Combine(folder, "Src", "Main.nt65"), FilePaths.AsStored(given));
        Assert.Equal(Path.Combine(folder, "Src", "Gone", "x.nt65"), FilePaths.AsStored(missing));
    }
}
