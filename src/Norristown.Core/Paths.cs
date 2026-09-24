namespace Norristown;

/// <summary>
/// Provides operations on logical paths, which are relative to the project's root and use
/// <c>/</c> separators on every platform. Sources, outputs and the files an <c>.incbin</c> names
/// are all identified by a logical path, so what nt65 writes is the same on every machine.
/// </summary>
public static class Paths
{
    /// <summary>
    /// Returns the directory part of a path without its trailing separator, or an empty string
    /// when the path has no directory part.
    /// </summary>
    public static string Directory(string path)
    {
        var at = path.LastIndexOf('/');
        return at < 0 ? "" : path[..at];
    }

    /// <summary>
    /// Returns <paramref name="path"/> with its <c>.</c> segments removed, along with every
    /// <c>..</c> that follows a directory. For example, <c>nt65/../data/x.bin</c> becomes
    /// <c>data/x.bin</c>. A path that starts above the root keeps the <c>..</c> segments that
    /// take it there.
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
    /// Returns the logical path that <paramref name="path"/> refers to when it appears in the file
    /// <paramref name="file"/>. The path is taken as relative to that file's directory unless it
    /// is rooted.
    /// </summary>
    public static string Beside(string file, string path) =>
        IsRooted(path) || Directory(file) is not { Length: > 0 } directory
            ? Normalized(path)
            : Normalized($"{directory}/{path}");

    /// <summary>
    /// Returns <paramref name="path"/> made relative to <paramref name="directory"/>, where both
    /// are relative to the same root. When the directory lies above the root, the route back down
    /// from it is unknown, so the path is returned unchanged. The path is also returned unchanged
    /// when either one is rooted.
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

    /// <summary>
    /// Returns a value indicating whether a path starts at a drive or at the root of the file
    /// system, rather than at the project.
    /// </summary>
    public static bool IsRooted(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') || (path.Length >= 2 && path[1] == ':');
}
