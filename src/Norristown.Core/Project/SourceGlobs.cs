using System.Text.RegularExpressions;

namespace Norristown.Project;

/// <summary>
/// The <c>files</c> globs of a project file. <c>**</c> matches any number of directories and has
/// to be a whole segment; anything else is a plain pattern for the directory it sits in, with
/// <c>*</c> and <c>?</c>. A glob may reach above the root, so that a library shared between
/// projects is part of each.
/// </summary>
public static class SourceGlobs
{
    /// <summary>The files on disk one glob names, as logical paths relative to <paramref name="root"/>.</summary>
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
    /// Whether one glob names <paramref name="path"/>, relative to <paramref name="root"/> or
    /// rooted, whether or not the file is on disk yet. An editor asks this of a file it has
    /// open but that was never saved.
    /// </summary>
    public static bool Matches(string root, string glob, string path)
    {
        var (from, pattern, search) = Split(root, glob);
        if (pattern.Contains('/'))
            return false;
        var full = Paths.Normalized(Path.GetFullPath(path, root));
        var directory = Paths.Normalized(from);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var within = search == SearchOption.AllDirectories
            ? full.StartsWith(directory + "/", comparison)
            : string.Equals(Paths.Directory(full), directory, comparison);
        if (!within)
            return false;

        var leaf = full[(full.LastIndexOf('/') + 1)..];
        var expression = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(leaf, expression,
            OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
    }

    /// <summary>The directory a glob searches, the pattern its files match, and whether it searches beneath.</summary>
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
