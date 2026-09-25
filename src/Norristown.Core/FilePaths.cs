using System.Text;

namespace Norristown;

/// <summary>
/// Provides the comparison the file system uses for two paths, which differs from how nt65
/// compares two names, and the spelling it stores a path with. Two paths that differ only in case are one file on Windows and on macOS,
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

    /// <summary>
    /// Returns a rooted path with each folder and file that exists spelled as the file system
    /// stores it. Where the file system ignores case, <c>SRC/MAIN.nt65</c> and
    /// <c>src/main.nt65</c> are one file, but nt65 would take them for two, each declaring the
    /// same module. The root, such as a drive, is kept as it was given, and so is the part of the
    /// path that does not exist and any path on a file system that respects case.
    /// </summary>
    /// <remarks>
    /// This reads the folders along the path, so a caller that asks often should keep the answers.
    /// </remarks>
    public static string AsStored(string path)
    {
        if (!IgnoresCase || Path.GetPathRoot(path) is not { Length: > 0 } root)
            return path;

        // The root is taken from the path rather than from GetPathRoot, which turns `/` into `\`.
        // A drive letter has no case the file system stores, and each client writes it its own way.
        var stored = new StringBuilder(path.Length);
        stored.Append(path, 0, root.Length);
        var directory = root;
        var start = root.Length;
        while (start < path.Length)
        {
            var end = path.IndexOfAny(['/', '\\'], start);
            if (end < 0)
                end = path.Length;
            if (Entry(directory, path[start..end]) is not { } name)
                break;
            stored.Append(name);
            if (end < path.Length)
                stored.Append(path[end]);
            directory = Path.Combine(directory, name);
            start = end + 1;
        }
        return stored.Append(path.AsSpan(Math.Min(start, path.Length))).ToString();
    }

    /// <summary>
    /// Returns the name of the entry of <paramref name="directory"/> that <paramref name="name"/>
    /// names, spelled as the file system stores it, or null when there is none.
    /// </summary>
    private static string? Entry(string directory, string name)
    {
        // A search pattern takes `*` and `?` as wildcards, and `.` and `..` are not entries.
        if (name is "" or "." or ".." || name.AsSpan().IndexOfAny('*', '?') >= 0)
            return null;
        try
        {
            return new DirectoryInfo(directory).EnumerateFileSystemInfos(name).FirstOrDefault()?.Name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
