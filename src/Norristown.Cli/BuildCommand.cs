using Norristown.Emit;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 build</c>: finds the project, reads the program, and writes its output, the C header
/// and the dependencies it was asked for.
/// <para>
/// Internally, every file is named by a path relative to the project root, the directory that
/// holds <c>nt65.json</c> and where a build normally runs: the sources, the outputs, and the
/// paths the output gives ca65 and the debugger. Paths in messages to the person running nt65
/// are relative to the directory they ran it from.
/// </para>
/// </summary>
public static class BuildCommand
{
    /// <summary>
    /// Builds what <paramref name="command"/> asks for, from <paramref name="directory"/>, and
    /// returns the exit code: 0 when it built, 1 when the program has errors, 2 when the command
    /// line is wrong.
    /// </summary>
    public static int Build(CommandLine command, string directory, TextWriter output, TextWriter error, bool colour) =>
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

        // `--stdout` prints the output of one file, so it needs exactly one file named.
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

        // Naming files still builds the whole program they are part of, so that names declared in
        // other files resolve as usual; only the named files' outputs are written.
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
            // When a build finds no files, an error in the project file is a far more common
            // cause than a missing `files`, so the project file's diagnostics are reported first.
            // The usage text is printed only when there are none, since then the command line is
            // what needs fixing.
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

        // Recorded so that, if nt65 crashes, its report says which program it was building.
        Building.Started(paths.Count == 1 ? paths[0] : $"{paths[0]} and {paths.Count - 1} more");
        var analysis = Compiler.Analyze(
            [.. sources.Select(SyntaxTree.Parse)], project, path => Length(Path.Combine(root, path)));
        var compilation = Compiler.Emit(analysis, project, header);
        Building.Nothing();

        // A watch watches the files the build read: the sources, and the binaries the outputs list
        // as dependencies. A program with errors may list no binaries, but the source that fixes
        // it is in the list either way.
        watched =
        [
            .. watched,
            .. paths.Select(path => Path.Combine(root, path)),
            .. compilation.Outputs.SelectMany(o => o.Dependencies).Distinct(StringComparer.Ordinal)
                .Select(dependency => Path.Combine(root, dependency)),
        ];

        foreach (var d in compilation.Diagnostics)
            Say(d);

        // `--stdout` prints the named file's output even when the rest of the program has
        // errors: the text a build would have written, with a note where it is incomplete. It is
        // the same text the editor's output preview shows, and no files are written.
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

        // `--check` asks only for the diagnostics, which have now been reported: it writes no
        // output, no header, no dependency file, and no record of what was written.
        if (command.Check)
            return new BuildResult(0, root, watched);

        // When files are named, write each output that contains one of them. A named module that
        // another module places in its own output is written as part of that output.
        var only = named.Count > 0 && project.Files.Count > 0 ? named.ToHashSet(StringComparer.Ordinal) : null;
        var written = compilation.Outputs
            .Where(o => only is null || o.AllSources.Any(source => only.Contains(source.Path)))
            .ToList();
        string[] extra = projectFile is null ? [] : [ProjectFile.Name];
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
                error.WriteLine($"nt65: note: deleted {ProjectRoot.Shown(directory, Path.Combine(root, deleted))}, which the program no longer writes");
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
    /// A span's file, relative to the directory nt65 was run from. A span whose file name is really
    /// a command-line option (starting with <c>-</c>) or a placeholder in parentheses for a place
    /// with no file of its own keeps that name unchanged.
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
        var written = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(written, text);
        File.Move(written, path, overwrite: true);
    }

    /// <summary>The length of a file an <c>.incbin</c> names, or null when it cannot be read.</summary>
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
