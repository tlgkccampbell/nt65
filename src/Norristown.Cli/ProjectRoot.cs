using Norristown.Project;

namespace Norristown.Cli;

/// <summary>
/// Where the program is and how its paths are spelled. Everything nt65 knows is named relative
/// to the root, the directory that holds <c>nt65.json</c>; everything it says is named relative
/// to where the person running it is.
/// </summary>
internal static class ProjectRoot
{
    /// <summary>The nearest project file at or above <paramref name="directory"/>, or null when there is none.</summary>
    public static string? Nearest(string directory)
    {
        for (var at = directory; at is not null; at = Path.GetDirectoryName(at))
        {
            var candidate = Path.Combine(at, ProjectFile.Name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>The logical path of <paramref name="full"/>: relative to <paramref name="root"/>, with <c>/</c> separators.</summary>
    public static string Logical(string root, string full) =>
        Paths.Normalized(Path.GetRelativePath(root, full));

    /// <summary>A path as the person running nt65 reads it: relative to where they ran it.</summary>
    public static string Shown(string directory, string full) =>
        Paths.Normalized(Path.GetRelativePath(directory, Path.GetFullPath(full)));
}
