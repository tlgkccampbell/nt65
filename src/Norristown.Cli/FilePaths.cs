namespace Norristown.Cli;

/// <summary>
/// How the file system tells two paths apart, which is not how nt65 tells two names apart: two
/// spellings that differ only in case are one file on Windows and on macOS, and two files
/// everywhere else. Anything that holds paths in a set or a dictionary holds them by this.
/// </summary>
internal static class FilePaths
{
    /// <summary>The comparer the file system this build is running on would use.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
