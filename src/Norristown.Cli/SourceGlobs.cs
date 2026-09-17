namespace Norristown.Cli;

/// <summary>The <c>files</c> globs of a project file, matched against the disk.</summary>
public static class SourceGlobs
{
    /// <summary>
    /// The files one glob names, as logical paths relative to <paramref name="root"/>. <c>**</c>
    /// matches any number of directories and has to be a whole segment; anything else is a plain
    /// pattern for the directory it sits in. A glob may reach above the root, so that a library
    /// shared between projects is part of each.
    /// </summary>
    public static IEnumerable<string> Matching(string root, string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var at = normalized.IndexOf("**/", StringComparison.Ordinal);
        var (start, pattern, search) = at >= 0
            ? (normalized[..at], normalized[(at + 3)..], SearchOption.AllDirectories)
            : (Paths.Directory(normalized), normalized.Split('/')[^1], SearchOption.TopDirectoryOnly);

        var from = Path.GetFullPath(start.Length == 0 ? "." : start, root);
        if (!Directory.Exists(from) || pattern.Contains('/'))
            return [];
        return Directory.EnumerateFiles(from, pattern, search)
            .Select(path => Paths.Normalized(Path.GetRelativePath(root, path)));
    }
}
