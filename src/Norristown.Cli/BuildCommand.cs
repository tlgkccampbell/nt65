using Norristown.Emit;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Cli;

/// <summary>
/// Implements <c>nt65 build</c>, which finds the project, reads the program, and writes its
/// output, along with the C header and the dependencies it was asked for.
/// <para>
/// Internally, every file is named by a path relative to the project root, which is the directory
/// that holds <c>nt65.json</c> and where a build normally runs. The sources, the outputs, and the
/// paths the output gives ca65 and the debugger are all named this way. Paths in messages to the
/// person running nt65 are relative to the directory they ran it from.
/// </para>
/// </summary>
public static class BuildCommand
{
    /// <summary>
    /// Builds what <paramref name="command"/> asks for, from <paramref name="directory"/>, and
    /// returns the exit code. The code is <see cref="ExitCode.Success"/> when the build succeeded,
    /// <see cref="ExitCode.InputError"/> when the program has errors, and
    /// <see cref="ExitCode.UsageError"/> when the command line is wrong.
    /// </summary>
    public static ExitCode Build(CommandLine command, string directory, TextWriter output, TextWriter error, bool colour) =>
        Run(command, directory, output, error, colour).Code;

    /// <summary>
    /// Runs one build and returns its exit code with the files it read, so that a watch knows
    /// which files to watch. A build that fails before it finds the program's sources lists only
    /// what it had read by then, which is nothing or the project file; the watch also reacts to
    /// any <c>.nt65</c> file under the root, so a source that appears is noticed anyway.
    /// </summary>
    internal static BuildResult Run(
        CommandLine command, string directory, TextWriter output, TextWriter error, bool colour)
    {
        try
        {
            return Built(command, directory, output, error, colour);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read or written is a problem with the files, not a bug in
            // nt65. On Windows it is common: an editor holds a source, or an emulator holds an
            // output. A watch goes on watching, and builds again once something changes.
            error.WriteLine($"nt65: error: {e.Message}");
            return new BuildResult(ExitCode.InputError, directory, []);
        }
    }

    /// <summary>
    /// Runs one build as <see cref="Run"/> does, letting a failure to read or write a file
    /// propagate to it.
    /// </summary>
    private static BuildResult Built(
        CommandLine command, string directory, TextWriter output, TextWriter error, bool colour)
    {
        // The project file is the one named, or the nearest one at or above where nt65 runs;
        // with none, the directory nt65 runs in is the root.
        var projectFile = ProjectRoot.Chosen(command.Project, directory);
        if (command.Project is not null && !File.Exists(projectFile))
        {
            error.WriteLine($"nt65: {ProjectRoot.Shown(directory, projectFile!)} does not exist");
            return new BuildResult(ExitCode.UsageError, directory, []);
        }
        var run = new Context(
            command, directory, projectFile is null ? directory : Path.GetDirectoryName(projectFile)!, output, error, colour);
        string[] watched = projectFile is null ? [] : [projectFile];

        // `--stdout` prints the output of one file, so it needs exactly one file named.
        if (command.Stdout && command.Files.Count != 1)
        {
            error.WriteLine("nt65: `--stdout` prints one file's output, so name exactly one file");
            error.WriteLine(CommandLine.SeeHelp);
            return new BuildResult(ExitCode.UsageError, run.Root, watched);
        }
        var project = ResolveProject(run, projectFile);
        watched = [.. watched, .. project.Links.Select(link => Path.Combine(run.Root, link.ConfigPath))];
        if (NamedFiles(run) is not { } named)
            return new BuildResult(ExitCode.InputError, run.Root, watched);
        var paths = project.Files.SelectMany(glob => SourceGlobs.Matching(run.Root, glob)).Concat(named)
            .Distinct(FilePaths.Comparer).Order(StringComparer.Ordinal).ToList();

        if (paths.Count == 0)
            return new BuildResult(ReportNoInput(run, project, projectFile), run.Root, watched);

        var header = command.Header is { } headerPath ? Path.GetFullPath(headerPath, directory) : null;
        var (analysis, compilation) = Compile(run, project, paths, header);

        // A watch watches the files the build read: the sources, and the binaries the outputs list
        // as dependencies. A program with errors may list no binaries, but the source that fixes
        // it is in the list either way.
        watched =
        [
            .. watched,
            .. paths.Select(path => Path.Combine(run.Root, path)),
            .. compilation.Outputs.SelectMany(o => o.Dependencies).Distinct(StringComparer.Ordinal)
                .Select(dependency => Path.Combine(run.Root, dependency)),
        ];

        Report(run, compilation.Diagnostics);
        var failed = compilation.Diagnostics.Any(d => d.Severity == Severity.Error);

        // `--stdout` prints the named file's output even when the rest of the program has
        // errors. It prints the text a build would have written, with a note where it is
        // incomplete. That is the same text the editor's output preview shows, and no files are
        // written.
        if (command.Stdout)
            return new BuildResult(Preview(run, analysis, project, named[0], failed), run.Root, watched);

        if (failed)
            return new BuildResult(ExitCode.InputError, run.Root, watched);
        if (compilation.IsCpuAssumed)
        {
            error.WriteLine($"nt65: note: nothing declares which processor this program is for, so it is built for the "
                + $"{CpuNames.Format(ProgramCpu.Default)}: give `--cpu`, `\"cpu\"` in {ProjectFile.Name}, or a `.cpu` item");
        }

        // `--check` asks only for the diagnostics, which have now been reported. It writes no
        // output, no header, no dependency file, and no record of what was written.
        if (!command.Check)
        {
            WriteArtifacts(run, project, compilation, named, paths, header,
                projectFile is null ? [] : [ProjectFile.Name, .. project.Links.Select(link => link.ConfigPath)]);
        }
        return new BuildResult(ExitCode.Success, run.Root, watched);
    }

    /// <summary>
    /// Returns the project's settings, read from <paramref name="projectFile"/> when there is one,
    /// with what the command line sets applied over them.
    /// </summary>
    private static ProjectSettings ResolveProject(Context run, string? projectFile)
    {
        var command = run.Command;
        var project = projectFile is null
            ? ProjectSettings.None
            : ProjectFile.Read(ProjectFile.Name, File.ReadAllText(projectFile), path => Linked(run.Root, path));

        var arguments = new List<Diagnostic>();
        if (command.Configuration is { } configuration)
            project = project.Configured(configuration, new Span("--config", 1, 1, configuration.Length + 1));
        // `--cpu` gives the processor only when the project does not, so a Makefile's default
        // does not override a project that knows which processor it is for.
        if (project.Cpu is null && command.Cpu is not null)
            project = project with { Cpu = command.Cpu };
        var values = command.Settings.Select(setting => ProjectFile.SettingValue(setting, arguments)).OfType<SettingValue>().ToList();
        project = project.With(values) with { Diagnostics = [.. project.Diagnostics, .. arguments] };
        if (command.Out is { } chosen)
            project = project with { Out = ProjectRoot.Logical(run.Root, Path.GetFullPath(chosen, run.Directory)) };
        return project;
    }

    /// <summary>
    /// Returns the text of the linker config at the logical path <paramref name="path"/>, or null
    /// when there is no such file.
    /// </summary>
    private static string? Linked(string root, string path)
    {
        var full = Path.Combine(root, path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    /// <summary>
    /// Returns the files the command line names, as paths relative to the project root, or null
    /// after reporting the first one that does not exist.
    /// </summary>
    private static List<string>? NamedFiles(Context run)
    {
        // Naming files still builds the whole program they are part of, so that names declared in
        // other files resolve as usual; only the named files' outputs are written.
        var named = new List<string>();
        foreach (var file in run.Command.Files)
        {
            var full = Path.GetFullPath(file, run.Directory);
            if (!File.Exists(full))
            {
                run.Error.WriteLine($"{file}: error: file not found");
                return null;
            }
            // Spelled as the file system stores it, the file is the one the project's globs find.
            named.Add(ProjectRoot.Logical(run.Root, FilePaths.AsStored(full)));
        }
        return named;
    }

    /// <summary>
    /// Reports that the build found no files to read, along with the project file's diagnostics,
    /// which are the likelier cause, and returns the exit code. The code is
    /// <see cref="ExitCode.InputError"/> when the project file has an error, and
    /// <see cref="ExitCode.UsageError"/> otherwise.
    /// </summary>
    private static ExitCode ReportNoInput(Context run, ProjectSettings project, string? projectFile)
    {
        // When a build finds no files, an error in the project file is a far more common
        // cause than a missing `files`, so the project file's diagnostics are reported first.
        // An error there is the whole story: the input is wrong, and fixing the file fixes the
        // build, which a watch goes on waiting for. The usage text is printed only when there
        // are no diagnostics, since then the command line is what needs fixing.
        Report(run, project.Diagnostics);
        if (project.Diagnostics.Any(d => d.Severity == Severity.Error))
            return ExitCode.InputError;
        run.Error.WriteLine(projectFile is null ? $"nt65: no input files, and no {ProjectFile.Name}"
            : project.Files.Count == 0 ? $"nt65: no input files, and no `files` in {ProjectFile.Name}"
            : $"nt65: no file matched the `files` globs in {ProjectFile.Name}");
        if (project.Diagnostics.Count == 0)
            run.Error.WriteLine(CommandLine.Usage);
        return ExitCode.UsageError;
    }

    /// <summary>
    /// Reads and analyzes the program's sources, and returns the analysis with the output it emits.
    /// </summary>
    private static (ProgramAnalysis Analysis, Compilation Compilation) Compile(
        Context run, ProjectSettings project, IReadOnlyList<string> paths, string? header)
    {
        var sources = paths.Select(path => new SourceFile(path, File.ReadAllText(Path.Combine(run.Root, path)))).ToList();

        // Recorded so that, if nt65 crashes, its report says which program it was building.
        Building.Started(paths.Count == 1 ? paths[0] : $"{paths[0]} and {paths.Count - 1} more");
        var analysis = Compiler.Analyze(
            [.. sources.Select(SyntaxTree.Parse)], project, path => Length(Path.Combine(run.Root, path)));
        var compilation = Compiler.Emit(analysis, project, header);
        Building.Nothing();
        return (analysis, compilation);
    }

    /// <summary>
    /// Reports each of <paramref name="diagnostics"/>, as a JSON object on standard output when
    /// <c>--json</c> asked for that, and as a line on standard error otherwise.
    /// </summary>
    private static void Report(Context run, IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            if (run.Command.Json)
                run.Output.WriteLine(Reported.Object(d, span => Named(run.Directory, run.Root, span)));
            else
                run.Error.WriteLine(Reported.Line(d, Named(run.Directory, run.Root, d.Span), run.Colour));
        }
    }

    /// <summary>
    /// Prints the output of the file <c>--stdout</c> names, and returns the exit code. The code is
    /// <see cref="ExitCode.InputError"/> when the file is not part of the program or the program
    /// has errors.
    /// </summary>
    private static ExitCode Preview(
        Context run, ProgramAnalysis analysis, ProjectSettings project, string named, bool failed)
    {
        if (OutputPreview.Of(analysis, project, named) is not { } preview)
        {
            run.Error.WriteLine($"{run.Command.Files[0]}: error: it is not a file of this program");
            return ExitCode.InputError;
        }
        run.Output.Write(preview.Text);
        return failed ? ExitCode.InputError : ExitCode.Success;
    }

    /// <summary>
    /// Writes the outputs, the C header and the dependency file that the build was asked for, and
    /// deletes the outputs the program no longer writes.
    /// </summary>
    /// <param name="run">The build being run.</param>
    /// <param name="project">The project's settings.</param>
    /// <param name="compilation">The output the program emitted.</param>
    /// <param name="named">The files the command line names, which limit the outputs written.</param>
    /// <param name="paths">The program's sources, which the header depends on.</param>
    /// <param name="header">The full path of the C header to write, or null for none.</param>
    /// <param name="extra">The project file's name when there is one, which every file written depends on.</param>
    private static void WriteArtifacts(
        Context run, ProjectSettings project, Compilation compilation, IReadOnlyList<string> named,
        IReadOnlyList<string> paths, string? header, string[] extra)
    {
        var (root, directory) = (run.Root, run.Directory);

        // When files are named, write each output that contains one of them. A named module that
        // another module places in its own output is written as part of that output.
        var only = named.Count > 0 && project.Files.Count > 0 ? named.ToHashSet(StringComparer.Ordinal) : null;
        var written = compilation.Outputs
            .Where(o => only is null || o.AllSources.Any(source => only.Contains(source.Path)))
            .ToList();
        foreach (var o in written)
            Write(Path.Combine(root, o.Path), o.Text, [.. o.Dependencies.Concat(extra).Select(dependency => Path.Combine(root, dependency))]);

        // A build of named files does not write every output, so only a whole-program build
        // updates the record of outputs and deletes the ones no longer written.
        if (only is null)
        {
            // Each deleted file is reported as a note, in the same `nt65: note:` form as the other
            // notes on standard error, so a script that reads standard error does not have to
            // recognise it separately. An output is deleted when its module has been removed or
            // is now placed in another module's output.
            foreach (var deleted in OutputManifest.Update(root, project.Out ?? ".", [.. compilation.Outputs.Select(o => o.Path)]))
                run.Error.WriteLine($"nt65: note: deleted {ProjectRoot.Shown(directory, Path.Combine(root, deleted))}, which the program no longer writes");
        }

        if (header is not null && compilation.Header is { } text)
            Write(header, text, [.. paths.Concat(extra).Select(path => Path.Combine(root, path))]);

        if (run.Command.DependencyFile is { } dependencyFile)
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
    }

    /// <summary>
    /// Returns a span's file, relative to the directory nt65 was run from. A span whose file name
    /// is really a command-line option (starting with <c>-</c>), or a placeholder in parentheses
    /// for a place with no file of its own, keeps that name unchanged.
    /// </summary>
    private static string Named(string directory, string root, Span span) =>
        span.File.StartsWith('-') || span.File.StartsWith('(')
            ? span.File
            : ProjectRoot.Shown(directory, Path.Combine(root, span.File));

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> only when it changes, so a build
    /// tool sees an unchanged file as unchanged. A file whose text is the same but which is older
    /// than one of <paramref name="dependencies"/> has its timestamp updated instead, or make would
    /// consider it out of date and run nt65 again on every build.
    /// <para>
    /// The text is written to a temporary file beside the target and then moved onto it, so a
    /// reader sees one build's complete file and never a mixture of two. A Makefile can easily run
    /// two builds into the same <c>out</c> by accident, and without this an assembler could read a
    /// file while it is being written.
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
        AtomicFile.Write(path, text);
    }

    /// <summary>
    /// Returns the length of a file an <c>.incbin</c> names, or null when it cannot be read.
    /// </summary>
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

    /// <summary>
    /// Represents one run of the build, which is what it was asked for and where it prints.
    /// </summary>
    /// <param name="Command">What the command line asked for.</param>
    /// <param name="Directory">The directory nt65 was run from, which messages name files relative to.</param>
    /// <param name="Root">The project root, or where nt65 ran when there is no project.</param>
    /// <param name="Output">The writer for standard output.</param>
    /// <param name="Error">The writer for standard error.</param>
    /// <param name="Colour">Whether diagnostics on standard error are coloured.</param>
    private sealed record Context(
        CommandLine Command, string Directory, string Root, TextWriter Output, TextWriter Error, bool Colour);
}
