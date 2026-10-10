using System.Text.RegularExpressions;

namespace Norristown.Project;

/// <summary>
/// Matches the <c>files</c> globs of a project file. In a glob, <c>**</c> matches any number of
/// directories. It has to be a whole segment and may be followed only by the file-name pattern.
/// The other directory segments are literal, and the file name is a pattern with <c>*</c> and
/// <c>?</c>. A glob may reach above the root, so that a library shared between projects is part
/// of each.
/// </summary>
public static class SourceGlobs
{
    /// <summary>
    /// Returns the files on disk that <paramref name="glob"/> names, as logical paths relative to
    /// <paramref name="root"/>.
    /// </summary>
    public static IEnumerable<string> Matching(string root, string glob)
    {
        var (from, pattern, search) = Split(root, glob);
        if (!Directory.Exists(from) || pattern.Contains('/'))
            return [];
        return Directory.EnumerateFiles(from, pattern, search)
            .Select(path => Paths.Normalized(Path.GetRelativePath(root, path)))
            .Where(path => Matches(root, glob, path));
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="glob"/> names <paramref name="path"/>,
    /// which is relative to <paramref name="root"/> or rooted, whether or not the file exists on
    /// disk yet. An editor asks this of a file it has open that was never saved.
    /// </summary>
    public static bool Matches(string root, string glob, string path) =>
        Matches(root, glob, path, FilePaths.IgnoresCase);

    /// <summary>
    /// Returns a value indicating whether <paramref name="glob"/> names <paramref name="path"/>
    /// on a file system that ignores case, or on one that respects it. The glob's directories and
    /// its file-name pattern follow the file system's rule, so a glob names the same files as the
    /// file system would find for it, and <see cref="Matching"/> agrees with this method on
    /// every platform. The public overload asks <see cref="FilePaths.IgnoresCase"/>.
    /// </summary>
    internal static bool Matches(string root, string glob, string path, bool ignoresCase)
    {
        var (from, pattern, search) = Split(root, glob);
        if (pattern.Contains('/'))
            return false;
        var full = Paths.Normalized(Path.GetFullPath(path, root));
        var directory = Paths.Normalized(from);
        var comparison = ignoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var within = search == SearchOption.AllDirectories
            ? full.StartsWith(directory + "/", comparison)
            : string.Equals(Paths.Directory(full), directory, comparison);
        if (!within)
            return false;

        var leaf = Paths.FileName(full);
        var expression = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        var options = ignoresCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant;
        return Regex.IsMatch(leaf, expression, options);
    }

    /// <summary>
    /// Returns the full path of the directory <paramref name="glob"/> searches, and whether it
    /// also searches that directory's subdirectories. A file that is created there may be a new
    /// file of the program, so this is what a watch has to watch.
    /// </summary>
    public static (string Directory, bool Recursive) Searched(string root, string glob)
    {
        var (from, _, search) = Split(root, glob);
        return (from, search == SearchOption.AllDirectories);
    }

    /// <summary>
    /// Splits a glob into the directory it searches, the pattern its files match, and whether it
    /// also searches subdirectories.
    /// </summary>
    private static (string From, string Pattern, SearchOption Search) Split(string root, string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var at = normalized.IndexOf("**/", StringComparison.Ordinal);
        var (start, pattern, search) = at >= 0
            ? (normalized[..at], normalized[(at + 3)..], SearchOption.AllDirectories)
            : (Paths.Directory(normalized), normalized.Split('/')[^1], SearchOption.TopDirectoryOnly);
        return (Path.GetFullPath(start.Length == 0 ? "." : start, root), pattern, search);
    }
}
