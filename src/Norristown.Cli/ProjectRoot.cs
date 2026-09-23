using Norristown.Project;

namespace Norristown.Cli;

/// <summary>
/// Finds the project and converts its paths. Internally, nt65 names files relative to the
/// project root, the directory that holds <c>nt65.json</c>; in its output, it names them
/// relative to the directory it was run from.
/// </summary>
internal static class ProjectRoot
{
    /// <summary>
    /// The project file a build reads: the one <c>--project</c> names, as a file or as the
    /// directory holding it, or the nearest one at or above <paramref name="directory"/>. A path
    /// given with <c>--project</c> is returned whether or not it exists, so that the error about
    /// it names the path the user gave.
    /// </summary>
    public static string? Chosen(string? project, string directory)
    {
        if (project is null)
            return Nearest(directory);
        var named = Path.GetFullPath(project, directory);
        return Directory.Exists(named) ? Path.Combine(named, ProjectFile.Name) : named;
    }

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
