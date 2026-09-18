using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 fmt</c>: writes the files it is given in the one layout nt65 sources are written in,
/// or, with <c>--check</c>, says which of them are not in it already.
/// <para>
/// It needs no program: formatting is a property of one file's own lines and braces, so a file
/// that names no module and belongs to no project still formats. Named nothing, it formats what
/// the project says the program is, which is what a repository-wide run wants.
/// </para>
/// </summary>
public static class FormatCommand
{
    /// <summary>
    /// Formats what <paramref name="arguments"/> names, from <paramref name="directory"/>, and
    /// returns the exit code: 0 when every file is formatted, 1 when <c>--check</c> found one
    /// that is not or a file could not be read, 2 when the command is wrong.
    /// </summary>
    public static int Run(IReadOnlyList<string> arguments, string directory, TextWriter output, TextWriter error)
    {
        var check = false;
        var named = new List<string>();
        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case "--check":
                    check = true;
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        error.WriteLine($"nt65: `{argument}` is not an option");
                        error.WriteLine(CommandLine.SeeHelp);
                        return 2;
                    }
                    named.Add(Path.GetFullPath(argument, directory));
                    break;
            }
        }

        var files = named;
        if (files.Count == 0)
        {
            var projectFile = ProjectRoot.Nearest(directory);
            if (projectFile is null)
            {
                error.WriteLine($"nt65: no files to format, and no {ProjectFile.Name}");
                error.WriteLine(CommandLine.Usage);
                return 2;
            }
            var root = Path.GetDirectoryName(projectFile)!;
            var project = ProjectFile.Read(ProjectFile.Name, File.ReadAllText(projectFile));
            files = [.. project.Files
                .SelectMany(glob => SourceGlobs.Matching(root, glob))
                .Select(path => Path.Combine(root, path))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
            if (files.Count == 0)
            {
                error.WriteLine(project.Files.Count == 0
                    ? $"nt65: no files to format, and no `files` in {ProjectFile.Name}"
                    : $"nt65: no file matched the `files` globs in {ProjectFile.Name}");
                return 2;
            }
        }

        var unformatted = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                error.WriteLine($"{ProjectRoot.Shown(directory, file)}: error: file not found");
                return 1;
            }

            var shown = ProjectRoot.Shown(directory, file);
            var text = File.ReadAllText(file);
            var formatted = Formatter.Format(SyntaxTree.Parse(shown, text));
            if (formatted == text)
                continue;
            unformatted++;
            if (check)
                output.WriteLine(shown);
            else
                File.WriteAllText(file, formatted);
        }
        return check && unformatted > 0 ? 1 : 0;
    }
}
