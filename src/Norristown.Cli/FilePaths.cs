namespace Norristown.Cli;

/// <summary>
/// Provides the comparison the file system uses for two paths, which differs from how nt65
/// compares two names. Two paths that differ only in case are one file on Windows and on macOS,
/// and two files everywhere else. Any set or dictionary keyed by file paths should use this
/// comparer.
/// </summary>
internal static class FilePaths
{
    /// <summary>Gets the comparer that the file system this build is running on would use.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
