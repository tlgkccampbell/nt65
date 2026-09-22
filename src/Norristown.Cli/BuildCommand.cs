using Norristown.Emit;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Syntax;

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
    public static int Build(CommandLine command, string directory, TextWriter output, TextWriter error, bool colour) =>
        Run(command, directory, output, error, colour).Code;

    /// <summary>
    /// One build, with what it read, so that a watch knows what to wait on. A failure that
    /// stopped before the program was found names what it had, which is nothing or the project
    /// file; the watch is watching the directory as well, so a file appearing is noticed anyway.
    /// </summary>
    internal static BuildResult Run(
        CommandLine command, string directory, TextWriter output, TextWriter error, bool colour)
    {
        // The project file is the one named, or the nearest one at or above where nt65 runs;
        // with none, the directory nt65 runs in is the root.
        var projectFile = ProjectRoot.Chosen(command.Project, directory);
        if (command.Project is not null && !File.Exists(projectFile))
        {
            error.WriteLine($"nt65: {ProjectRoot.Shown(directory, projectFile!)} does not exist");
            return new BuildResult(2, directory, []);
        }
        var root = projectFile is null ? directory : Path.GetDirectoryName(projectFile)!;
        string[] watched = projectFile is null ? [] : [projectFile];

        // `--stdout` answers what one file became, so it is one file it is asked about.
        if (command.Stdout && command.Files.Count != 1)
        {
            error.WriteLine("nt65: --stdout writes one file's output, so it takes one file");
            error.WriteLine(CommandLine.SeeHelp);
            return new BuildResult(2, root, watched);
        }
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
                return new BuildResult(1, root, watched);
            }
            named.Add(ProjectRoot.Logical(root, full));
        }
        var paths = project.Files.SelectMany(glob => SourceGlobs.Matching(root, glob)).Concat(named)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        void Say(Diagnostic d)
        {
            if (command.Json)
                output.WriteLine(Reported.Object(d, span => Named(directory, root, span)));
            else
                error.WriteLine(Reported.Line(d, Named(directory, root, d.Span), colour));
        }

        if (paths.Count == 0)
        {
            // A project file with something wrong with it is why a build finds no files far
            // more often than a missing `files` is, so what is wrong with it is said first,
            // and the usage is left for when the command line is what there is to fix.
            foreach (var d in project.Diagnostics)
                Say(d);
            error.WriteLine(projectFile is null ? $"nt65: no input files, and no {ProjectFile.Name}"
                : project.Files.Count == 0 ? $"nt65: no input files, and no `files` in {ProjectFile.Name}"
                : $"nt65: no file matched the `files` globs in {ProjectFile.Name}");
            if (project.Diagnostics.Count == 0)
                error.WriteLine(CommandLine.Usage);
            return new BuildResult(2, root, watched);
        }

        var sources = paths.Select(path => new SourceFile(path, File.ReadAllText(Path.Combine(root, path)))).ToList();
        var header = command.Header is { } headerPath ? Path.GetFullPath(headerPath, directory) : null;

        // What a crash names, so that a report says which program nt65 was reading.
        Building.Started(paths.Count == 1 ? paths[0] : $"{paths[0]} and {paths.Count - 1} more");
        var analysis = Compiler.Analyze(
            [.. sources.Select(SyntaxTree.Parse)], project, path => Length(Path.Combine(root, path)));
        var compilation = Compiler.Emit(analysis, project, header);
        Building.Nothing();

        // What a watch waits on is what the build read: the sources, and the binaries the outputs
        // say they include. A program that is wrong writes no output and so names no binaries;
        // the source that fixes it is in the list either way.
        watched =
        [
            .. watched,
            .. paths.Select(path => Path.Combine(root, path)),
            .. compilation.Outputs.SelectMany(o => o.Dependencies).Distinct(StringComparer.Ordinal)
                .Select(dependency => Path.Combine(root, dependency)),
        ];

        foreach (var d in compilation.Diagnostics)
            Say(d);

        // `--stdout` answers what the named file became, whatever is wrong with the rest of the
        // program: what a build would have written, under a note where it is incomplete. It is
        // the same text the editor shows beside the source, and it writes no files.
        if (command.Stdout)
        {
            if (OutputPreview.Of(analysis, project, named[0]) is not { } preview)
            {
                error.WriteLine($"{command.Files[0]}: error: it is not a file of this program");
                return new BuildResult(1, root, watched);
            }
            output.Write(preview.Text);
            return new BuildResult(
                compilation.Diagnostics.Any(d => d.Severity == Severity.Error) ? 1 : 0, root, watched);
        }

        if (compilation.Diagnostics.Any(d => d.Severity == Severity.Error))
            return new BuildResult(1, root, watched);
        if (compilation.IsCpuAssumed)
        {
            error.WriteLine($"nt65: note: nothing says which processor this program is for, so it is built for the "
                + $"{CpuNames.Spell(ProgramCpu.Default)}: give `--cpu`, `\"cpu\"` in {ProjectFile.Name}, or a `.cpu` item");
        }

        // `--check` asked what is wrong, which has now been said, and for nothing else: no
        // output, no header, no dependency file, and no record of what was written.
        if (command.Check)
            return new BuildResult(0, root, watched);

        var only = named.Count > 0 && project.Files.Count > 0 ? named.ToHashSet(StringComparer.Ordinal) : null;
        var written = compilation.Outputs.Where(o => only is null || only.Contains(o.Source)).ToList();
        string[] extra = projectFile is null ? [] : [ProjectFile.Name];
        foreach (var o in written)
            Write(Path.Combine(root, o.Path), o.Text, [.. o.Dependencies.Concat(extra).Select(dependency => Path.Combine(root, dependency))]);

        // A partial build leaves alone what it did not write, so only a whole one keeps the record.
        if (only is null)
        {
            // A build that worked says what it tidied up as the note it is: what else goes to
            // stderr is a diagnostic, and a script that reads stderr for those should not have
            // to know this one apart.
            foreach (var deleted in OutputManifest.Update(root, project.Out ?? ".", [.. compilation.Outputs.Select(o => o.Path)]))
                error.WriteLine($"nt65: note: deleted {ProjectRoot.Shown(directory, Path.Combine(root, deleted))}, whose module is not in the program");
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
        return new BuildResult(0, root, watched);
    }

    /// <summary>
    /// A span's file as the person running nt65 would write it. One whose file is an option or a
    /// place with no file of its own keeps the name it was given.
    /// </summary>
    private static string Named(string directory, string root, Span span) =>
        span.File.StartsWith('-') || span.File.StartsWith('(')
            ? span.File
            : ProjectRoot.Shown(directory, Path.Combine(root, span.File));

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> only when it changes, so a build
    /// tool sees an unchanged file as unchanged. One whose text is the same but that is older than
    /// something in <paramref name="dependencies"/> is touched instead, or make would run nt65 for
    /// it on every build.
    /// <para>
    /// It is written beside its place and moved onto it, so that whoever reads it sees one
    /// build's file or another's and never half of each: two builds into one <c>out</c> is
    /// something a Makefile does by accident, and an assembler reading the file while it is
    /// written is what would come of it.
    /// </para>
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
        var written = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(written, text);
        File.Move(written, path, overwrite: true);
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
