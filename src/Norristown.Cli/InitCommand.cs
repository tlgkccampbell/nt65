using System.Text;
using System.Text.Json;
using Norristown.Processor;
using Norristown.Project;

namespace Norristown.Cli;

/// <summary>
/// Implements <c>nt65 init</c>, which writes the two files a minimal program consists of, an
/// <c>nt65.json</c> and a <c>src/main.nt65</c>, so that a newcomer's first build works without
/// them having to copy a project file out of the documentation.
/// <para>
/// When the directory already holds ld65 linker configs, the project links them, so that they
/// declare its segments. One config is the project's link. Several are taken to be alternative
/// targets, so each becomes a named configuration that links it.
/// </para>
/// <para>
/// It refuses to overwrite either file, and writes neither if one already exists, so running it
/// in a directory that already holds a program leaves that program alone.
/// </para>
/// </summary>
public static class InitCommand
{
    private const string Out = "build";

    private const string Code = "CODE";

    private const string Source = """
        ; The program. `nt65 build` writes its ca65 under the project's `out`, for ca65 to
        ; assemble and ld65 to link.
        .module main

        .export main

        .segment SEGMENT
        .proc main {
            rts
        }

        """;

    /// <summary>
    /// Writes a project into the directory <paramref name="arguments"/> names, or into
    /// <paramref name="directory"/>, and returns the exit code. The code is 0 when it wrote both
    /// files, 1 when one of them was there already, and 2 when the command is wrong.
    /// </summary>
    public static ExitCode Run(IReadOnlyList<string> arguments, string directory, TextWriter output, TextWriter error)
    {
        var cpu = Cpu.Mos6502;
        string? where = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--cpu":
                    var value = i + 1 < arguments.Count ? arguments[++i] : null;
                    if (value is null || CpuNames.Parse(value) is not { } named)
                    {
                        error.WriteLine(value is null
                            ? $"nt65: `--cpu` needs a processor: {CpuNames.Listed.Replace("`", "", StringComparison.Ordinal)}"
                            : $"nt65: `{value}` is not a processor nt65 knows; `--cpu` takes {CpuNames.Listed.Replace("`", "", StringComparison.Ordinal)}");
                        error.WriteLine(CommandLine.SeeHelp);
                        return ExitCode.UsageError;
                    }
                    cpu = named;
                    break;
                default:
                    if (arguments[i].StartsWith('-') || where is not null)
                    {
                        error.WriteLine(arguments[i].StartsWith('-')
                            ? $"nt65: `{arguments[i]}` is not an option"
                            : "nt65: `init` takes at most one directory");
                        error.WriteLine(CommandLine.SeeHelp);
                        return ExitCode.UsageError;
                    }
                    where = arguments[i];
                    break;
            }
        }

        var root = Path.GetFullPath(where ?? ".", directory);
        var project = Path.Combine(root, ProjectFile.Name);
        var main = Path.Combine(root, "src", "main.nt65");

        // Both are checked before either is written, so a directory that holds one of them is
        // left exactly as it was.
        foreach (var existing in (ReadOnlySpan<string>)[project, main])
        {
            if (File.Exists(existing))
            {
                error.WriteLine($"nt65: {ProjectRoot.Shown(directory, existing)} exists already");
                return ExitCode.InputError;
            }
        }

        var linked = Linked(root);
        var segment = CodeSegment(linked, error);
        Directory.CreateDirectory(Path.GetDirectoryName(main)!);
        File.WriteAllText(project, Describing(cpu, linked));
        File.WriteAllText(main, Source.Replace("SEGMENT", segment, StringComparison.Ordinal).ReplaceLineEndings("\n"));
        output.WriteLine(ProjectRoot.Shown(directory, project));
        output.WriteLine(ProjectRoot.Shown(directory, main));
        foreach (var link in linked)
            output.WriteLine(linked.Count == 1 ? $"links {link.Path}" : $"configuration {link.Name} links {link.Path}");
        return ExitCode.Success;
    }

    /// <summary>
    /// Returns the project file as <c>init</c> writes it, with the three keys a program needs,
    /// and the links to the configs in <paramref name="linked"/>.
    /// </summary>
    private static string Describing(Cpu cpu, IReadOnlyList<Found> linked)
    {
        var text = new StringBuilder();
        text.Append('{').Append('\n');
        text.Append($"  \"cpu\": \"{CpuNames.Format(cpu)}\",\n");
        text.Append("  \"files\": [\"src/**/*.nt65\"],\n");
        text.Append($"  \"out\": \"{Out}\"");
        if (linked is [var only])
        {
            text.Append(",\n  \"links\": {\n");
            text.Append($"    {Quoted(only.Name)}: {{ \"config\": {Quoted(only.Path)} }}\n");
            text.Append("  }");
        }
        else if (linked.Count > 1)
        {
            text.Append(",\n  \"configurations\": {\n");
            for (var i = 0; i < linked.Count; i++)
            {
                var (name, path, _) = linked[i];
                text.Append($"    {Quoted(name)}: {{\n");
                text.Append($"      \"links\": {{ {Quoted(name)}: {{ \"config\": {Quoted(path)} }} }},\n");
                text.Append($"      \"out\": {Quoted($"{Out}/{name}")}\n");
                text.Append(i < linked.Count - 1 ? "    },\n" : "    }\n");
            }
            text.Append("  }");
        }
        text.Append("\n}\n");
        return text.ToString();
    }

    /// <summary>
    /// Returns the ld65 configs in <paramref name="root"/> and the folders beneath it, ordered by
    /// path, each with the name its link and configuration take. A <c>.cfg</c> file that is not an
    /// ld65 config nt65 can read, or that places no segments, is not one.
    /// </summary>
    private static List<Found> Linked(string root)
    {
        var found = new List<Found>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in ConfigFiles(root).Select(file => Paths.Normalized(Path.GetRelativePath(root, file))).Order(StringComparer.Ordinal))
        {
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(root, file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var config = LinkerConfig.Parse(file, text);
            if (config.Diagnostics.Count > 0 || config.Segments.Count == 0)
                continue;

            // A configuration's name is letters, digits, `_` and `-`, and two configs with the
            // same file name in different folders take different names.
            var stem = new string([.. Path.GetFileNameWithoutExtension(file)
                .Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-')]);
            if (stem.Length == 0)
                stem = "link";
            var name = stem;
            for (var n = 2; !names.Add(name); n++)
                name = $"{stem}-{n}";
            found.Add(new Found(name, file, config));
        }
        return found;
    }

    /// <summary>
    /// Returns every <c>.cfg</c> file in <paramref name="root"/> and the folders beneath it. Hidden
    /// folders, <c>node_modules</c> and the build output are not searched.
    /// </summary>
    private static List<string> ConfigFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*.cfg"));
                foreach (var folder in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(folder);
                    if (!name.StartsWith('.') && name != "node_modules" && !(name == Out && directory == root))
                        pending.Push(folder);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read holds nothing init can link.
            }
        }
        return files;
    }

    /// <summary>
    /// Returns the segment <c>src/main.nt65</c> puts its routine in. That is <c>CODE</c> when every
    /// linked config places it. When the one config does not, it is the first segment the config
    /// places for code or read-only data. With several configs the project's own build links
    /// none and needs a standard segment, so a config without <c>CODE</c> is reported instead.
    /// </summary>
    private static string CodeSegment(IReadOnlyList<Found> linked, TextWriter error)
    {
        var lacking = linked.Where(link => !link.Config.Segments.Any(segment => segment.Name == Code)).ToList();
        if (lacking.Count == 0)
            return Code;
        if (linked.Count == 1
            && linked[0].Config.Segments.FirstOrDefault(segment => segment.Type is null or "ro") is { } readOnly)
        {
            return readOnly.Name;
        }
        foreach (var link in lacking)
        {
            error.WriteLine($"nt65: note: {link.Path} places no `{Code}` segment, so src/main.nt65 does not link "
                + "with it until its routine is in a segment the config places");
        }
        return Code;
    }

    private static string Quoted(string text) => JsonSerializer.Serialize(text);

    /// <summary>Represents an ld65 config that <c>init</c> found, and the name it links it by.</summary>
    /// <param name="Name">The name of its link, and of its configuration when there are several.</param>
    /// <param name="Path">Its path from the project root.</param>
    /// <param name="Config">The config as nt65 reads it.</param>
    private sealed record Found(string Name, string Path, LinkerConfig Config);
}
