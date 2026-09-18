using Norristown.Project;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 build</c>: finds the project, reads the program, and writes its output, the C header
/// and the dependencies it was asked for.
/// <para>
/// Everything is known by a path relative to the project's root, the directory that holds
/// <c>nt65.json</c>, which is where a build normally runs: sources, outputs, and what the
/// output tells ca65 and the debugger. What is said to the person running it is relative to
/// where they are.
/// </para>
/// </summary>
public static class BuildCommand
{
    /// <summary>
    /// Builds what <paramref name="command"/> asks for, from <paramref name="directory"/>, and
    /// returns the exit code: 0 when it built, 1 when the program is wrong, 2 when the command is.
    /// </summary>
    public static int Build(CommandLine command, string directory, TextWriter error)
    {
        // The project file is the one named, or the nearest one at or above where nt65 runs;
        // with none, the directory nt65 runs in is the root.
        var projectFile = command.Project is { } given
            ? Path.GetFullPath(given, directory) is var chosenPath && Directory.Exists(chosenPath)
                ? Path.Combine(chosenPath, ProjectFile.Name)
                : chosenPath
            : ProjectRoot.Nearest(directory);
        if (command.Project is not null && !File.Exists(projectFile))
        {
            error.WriteLine($"nt65: {ProjectRoot.Shown(directory, projectFile!)} does not exist");
            return 2;
        }
        var root = projectFile is null ? directory : Path.GetDirectoryName(projectFile)!;
        var project = projectFile is null
            ? ProjectSettings.None
            : ProjectFile.Read(ProjectFile.Name, File.ReadAllText(projectFile));

        var arguments = new List<Diagnostic>();
        if (command.Configuration is { } configuration)
            project = project.Configured(configuration, new Span("--config", 1, 1, configuration.Length + 1));
        if (command.Cpu is not null)
            project = project with { Cpu = command.Cpu };
        var defines = command.Defines.Select(define => ProjectFile.Definition(define, arguments)).OfType<Define>().ToList();
        project = project.With(defines) with { Diagnostics = [.. project.Diagnostics, .. arguments] };
        if (command.Out is { } chosen)
            project = project with { Out = ProjectRoot.Logical(root, Path.GetFullPath(chosen, directory)) };

        // Naming files builds the program they are part of, so that a name another file declares
        // still means what it means; only the named files are written.
        var named = new List<string>();
        foreach (var file in command.Files)
        {
            var full = Path.GetFullPath(file, directory);
            if (!File.Exists(full))
            {
                error.WriteLine($"{file}: error: file not found");
                return 1;
            }
            named.Add(ProjectRoot.Logical(root, full));
        }
        var paths = project.Files.SelectMany(glob => SourceGlobs.Matching(root, glob)).Concat(named)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
        {
            error.WriteLine(projectFile is null ? $"nt65: no input files, and no {ProjectFile.Name}"
                : project.Files.Count == 0 ? $"nt65: no input files, and no `files` in {ProjectFile.Name}"
                : $"nt65: no file matched the `files` globs in {ProjectFile.Name}");
            error.WriteLine(CommandLine.Usage);
            return 2;
        }

        var sources = paths.Select(path => new SourceFile(path, File.ReadAllText(Path.Combine(root, path)))).ToList();
        var header = command.Header is { } headerPath ? Path.GetFullPath(headerPath, directory) : null;
        var compilation = Compiler.Compile(sources, project, path => Length(Path.Combine(root, path)), header);

        foreach (var d in compilation.Diagnostics)
        {
            var file = d.Span.File.StartsWith('-') || d.Span.File.StartsWith('(')
                ? d.Span.File
                : ProjectRoot.Shown(directory, Path.Combine(root, d.Span.File));
            error.WriteLine($"{file}:{d.Span.Line}:{d.Span.StartColumn}: {d.Severity.ToString().ToLowerInvariant()}: {d.Message}");
        }
        if (compilation.Diagnostics.Any(d => d.Severity == Severity.Error))
            return 1;
        if (compilation.IsCpuAssumed)
        {
            error.WriteLine($"nt65: note: nothing says which processor this program is for, so it is built for the "
                + $"{CpuNames.Spell(ProgramCpu.Default)}: give `--cpu`, `\"cpu\"` in {ProjectFile.Name}, or a `.cpu` item");
        }

        var only = named.Count > 0 && project.Files.Count > 0 ? named.ToHashSet(StringComparer.Ordinal) : null;
        var written = compilation.Outputs.Where(o => only is null || only.Contains(o.Source)).ToList();
        string[] extra = projectFile is null ? [] : [ProjectFile.Name];
        foreach (var o in written)
            Write(Path.Combine(root, o.Path), o.Text, [.. o.Dependencies.Concat(extra).Select(dependency => Path.Combine(root, dependency))]);

        // A partial build leaves alone what it did not write, so only a whole one keeps the record.
        if (only is null)
        {
            foreach (var deleted in OutputManifest.Update(root, project.Out ?? ".", [.. compilation.Outputs.Select(o => o.Path)]))
                error.WriteLine($"nt65: deleted {ProjectRoot.Shown(directory, Path.Combine(root, deleted))}, whose module is not in the program");
        }

        if (header is not null && compilation.Header is { } text)
            Write(header, text, [.. paths.Concat(extra).Select(path => Path.Combine(root, path))]);

        if (command.DependencyFile is { } dependencyFile)
        {
            List<(string, IEnumerable<string>)> rules =
            [
                .. written.Select(o => (ProjectRoot.Shown(directory, Path.Combine(root, o.Path)),
                    o.Dependencies.Concat(extra).Select(dependency => ProjectRoot.Shown(directory, Path.Combine(root, dependency))))),
            ];
            if (header is not null)
                rules.Add((ProjectRoot.Shown(directory, header), paths.Concat(extra).Select(path => ProjectRoot.Shown(directory, Path.Combine(root, path)))));
            Write(Path.GetFullPath(dependencyFile, directory), DependencyFile.Write(rules), []);
        }
        return 0;
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> only when it changes, so a build
    /// tool sees an unchanged file as unchanged. One whose text is the same but that is older than
    /// something in <paramref name="dependencies"/> is touched instead, or make would run nt65 for
    /// it on every build.
    /// </summary>
    private static void Write(string path, string text, IReadOnlyList<string> dependencies)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            var at = File.GetLastWriteTimeUtc(path);
            if (dependencies.Any(dependency => File.Exists(dependency) && File.GetLastWriteTimeUtc(dependency) > at))
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return;
        }
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
            Directory.CreateDirectory(parent);
        File.WriteAllText(path, text);
    }

    /// <summary>How long a file an <c>.incbin</c> names is, or null when it cannot be read.</summary>
    private static long? Length(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
