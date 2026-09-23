namespace Norristown;

/// <summary>
/// Logical paths: relative to the project's root, with <c>/</c> separators whatever the
/// platform. Sources, outputs and the files an <c>.incbin</c> names are all identified by a
/// logical path, so what nt65 writes is the same on every machine.
/// </summary>
public static class Paths
{
    /// <summary>The directory part of a path, without its trailing separator, or empty for none.</summary>
    public static string Directory(string path)
    {
        var at = path.LastIndexOf('/');
        return at < 0 ? "" : path[..at];
    }

    /// <summary>
    /// <paramref name="path"/> with its <c>.</c> segments and every <c>..</c> that follows a
    /// directory taken out: <c>nt65/../data/x.bin</c> is <c>data/x.bin</c>. A path that
    /// starts above the root keeps the <c>..</c> that take it there.
    /// </summary>
    public static string Normalized(string path)
    {
        var rooted = IsRooted(path);
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "." || (part.Length == 0 && parts.Count > 0))
                continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != ".." && !(rooted && parts.Count == 1))
                parts.RemoveAt(parts.Count - 1);
            else
                parts.Add(part);
        }
        return string.Join('/', parts);
    }

    /// <summary>
    /// The logical path that <paramref name="path"/> refers to when it is written in the file
    /// <paramref name="file"/>: relative to that file's directory unless it is rooted.
    /// </summary>
    public static string Beside(string file, string path) =>
        IsRooted(path) || Directory(file) is not { Length: > 0 } directory
            ? Normalized(path)
            : Normalized($"{directory}/{path}");

    /// <summary>
    /// <paramref name="path"/> made relative to <paramref name="directory"/>, where both are
    /// relative to the same root. When the directory lies above the root, the route back down
    /// from it is unknown, so the path is returned unchanged, as it is when either is rooted.
    /// </summary>
    public static string Relative(string directory, string path)
    {
        if (IsRooted(path) || IsRooted(directory))
            return path;
        var from = directory.Length == 0 ? [] : Normalized(directory).Split('/');
        var to = Normalized(path).Split('/');
        var shared = 0;
        while (shared < from.Length && shared < to.Length - 1 && from[shared] == to[shared])
            shared++;
        if (from.AsSpan(shared).Contains(".."))
            return path;
        return string.Join('/', Enumerable.Repeat("..", from.Length - shared).Concat(to[shared..]));
    }

    /// <summary>Whether a path starts at a drive or at the root of the file system, rather than at the project.</summary>
    public static bool IsRooted(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') || (path.Length >= 2 && path[1] == ':');
}
