namespace Norristown.Cli;

/// <summary>
/// How the file system compares two paths, which differs from how nt65 compares two names: two
/// spellings that differ only in case are one file on Windows and on macOS, and two files
/// everywhere else. Any set or dictionary keyed by file paths should use this comparer.
/// </summary>
internal static class FilePaths
{
    /// <summary>The comparer the file system this build is running on would use.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
