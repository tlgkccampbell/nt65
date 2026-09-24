namespace Norristown;

/// <summary>
/// Provides the comparison the file system uses for two paths, which differs from how nt65
/// compares two names. Two paths that differ only in case are one file on Windows and on macOS,
/// and two files everywhere else. Any set or dictionary keyed by file paths, and any test of
/// whether two paths are the same file, should use this rule.
/// </summary>
public static class FilePaths
{
    /// <summary>
    /// Gets a value indicating whether the file system this build is running on treats two paths
    /// that differ only in case as one file.
    /// </summary>
    public static bool IgnoresCase { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Gets the comparer that the file system this build is running on would use.</summary>
    public static StringComparer Comparer { get; } =
        IgnoresCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Gets the comparison that the file system this build is running on would use.</summary>
    public static StringComparison Comparison { get; } =
        IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
