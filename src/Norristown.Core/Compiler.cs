using System.Collections.Concurrent;
using Norristown.Emit;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Standard;
using Norristown.Syntax;

namespace Norristown;

/// <summary>
/// Runs the whole pipeline, from a set of source files to ca65 output and diagnostics. The
/// order of <c>files</c> must not affect the result.
/// </summary>
public static class Compiler
{
    // How many times a program is composed again for files analyzed with stack effects it turned
    // out to differ on, before what was found is kept as it stands.
    private const int StaleRounds = 4;

    /// <summary>
    /// Compiles <paramref name="files"/> as one program, and returns its ca65 output and
    /// diagnostics.
    /// </summary>
    /// <param name="files">The program's source files.</param>
    /// <param name="project">
    /// The settings the program is built with, or null to build it with no project file. A CPU
    /// that the settings name must agree with any <c>.cpu</c> directive in the source.
    /// </param>
    /// <param name="binaryLength">
    /// The function that returns the length of each file an <c>.incbin</c> names, or null to read
    /// the length from disk. A caller whose files are not at the paths the source gives passes its
    /// own function.
    /// </param>
    /// <param name="cHeader">
    /// The file to write a C header of what the program exports to, or null to write none.
    /// </param>
    public static Compilation Compile(
        IReadOnlyCollection<SourceFile> files, ProjectSettings? project = null, Func<string, long?>? binaryLength = null,
        string? cHeader = null)
    {
        project ??= ProjectSettings.None;
        return Emit(Analyze([.. files.Select(SyntaxTree.Parse)], project, binaryLength), project, cHeader);
    }

    /// <summary>
    /// Emits the program that <paramref name="analysis"/> describes, and a C header of what it
    /// exports when <paramref name="cHeader"/> names the file for the header.
    /// </summary>
    public static Compilation Emit(ProgramAnalysis analysis, ProjectSettings project, string? cHeader = null)
    {
        var diagnostics = new List<Diagnostic>(analysis.Diagnostics);
        var outputs = new List<OutputFile>();
        var direct = new Dictionary<SyntaxTree, HashSet<string>>();

        // Every file is written even when the program already has errors, because what emission
        // finds — a construct no earlier stage checked, two names that collide in the output —
        // is worth reporting alongside the rest rather than only once the rest is fixed.
        var measured = OutputNames.MeasuredElsewhere(analysis);
        foreach (var file in analysis.Files)
        {
            var model = file.Model;

            // The modules that come with nt65 are not files anyone wrote, and nothing is written
            // for them. A module that another module places has no output of its own: it is
            // written into the output of its translation unit.
            if (StandardModules.IsStandard(model.Tree.Path)
                || analysis.Placements.PlacerOf(model.Tree) is not null)
                continue;
            var members = analysis.Placements.UnitOf(model.Tree)?.Members ?? [model.Tree];
            var written = EmitFile(analysis, project, file, measured, diagnostics);

            // A file that would hold only its header is left out, because an empty object file
            // is one more thing to assemble and link for nothing.
            if (written.IsEmpty)
                continue;
            var output = written with
            {
                Dependencies = [.. members
                    .SelectMany(member => analysis.ModelFor(member.Path) is { } file ? Dependencies(analysis.Program, file, direct) : [])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)],
            };
            outputs.Add(output);

            // The map of which source line each output line came from goes in a file beside the
            // output rather than into it, so that the ca65 holds only the program; `nt65
            // remap-dbg` puts the map into ld65's debug file after the link.
            if (LineMap.For(output) is { } map)
                outputs.Add(map);
        }
        var header = cHeader is null ? null : CHeader.Write(analysis.Program, cHeader, diagnostics);

        // An `.allow` may hide a warning that only emission finds. The analysis reported it as
        // hiding nothing, so that report is taken back.
        var allowances = Allowances.Of(analysis.Program);
        var used = new HashSet<Allowance>();
        var emitted = allowances.Apply(diagnostics.Skip(analysis.Diagnostics.Count), used).ToList();
        var unused = used.Select(allowance => allowance.Directive.Tree.GetSpan(allowance.Directive.Span)).ToHashSet();
        diagnostics = [.. analysis.Diagnostics
            .Where(d => d.Id != Catalogue.AllowUnused.Id || !unused.Contains(d.Span))
            .Concat(emitted)];

        // A program with errors produces no output: what would be written for it is not a
        // translation of anything. The project can change the severity of each diagnostic by
        // name, so its settings are applied before the severities are checked for errors.
        var ordered = Diagnostics.Ordered(Diagnostics.WithSeverities(diagnostics, project.Severities));
        var wrong = ordered.Any(d => d.Severity == Severity.Error);
        return new Compilation(wrong ? [] : outputs, ordered)
        {
            Header = wrong ? null : header,
            IsCpuAssumed = project.Cpu is null && !ProgramCpu.IsStated(analysis.Program.Files.Select(file => file.Tree)),
        };
    }

    /// <summary>
    /// Returns the ca65 output for one file of <paramref name="analysis"/>, even when the rest of
    /// the program has errors, or null when the program has no such file. A build writes nothing
    /// for a program with errors. The editor shows what would have been written anyway, so that
    /// seeing what a line became does not wait for the rest of the file to be correct. For a
    /// module that another module places, this returns the output of the translation unit that
    /// contains it.
    /// </summary>
    /// <param name="analysis">The program the file belongs to.</param>
    /// <param name="project">The project it is built as, whose <c>out</c> names where the file goes.</param>
    /// <param name="path">The logical path of the source file to emit.</param>
    public static OutputFile? EmitFile(ProgramAnalysis analysis, ProjectSettings project, string path)
    {
        if (analysis.ModelFor(path) is not { } model || StandardModules.IsStandard(path))
            return null;
        var root = analysis.Placements.UnitOf(model.Tree)?.Root.Path ?? path;
        return analysis.FileFor(root) is { } file ? EmitFile(analysis, project, file, OutputNames.MeasuredElsewhere(analysis), []) : null;
    }

    /// <summary>
    /// Parses <paramref name="files"/> and analyzes them as one program, without emitting
    /// anything. An editor calls this, and
    /// <see cref="Emit(ProgramAnalysis, ProjectSettings, string?)"/> emits from its result.
    /// </summary>
    /// <param name="files">The program's source files.</param>
    /// <param name="project">The settings the program is built with.</param>
    /// <param name="binaryLength">
    /// The function that returns the length of each file an <c>.incbin</c> names, or null to read
    /// the length from disk.
    /// </param>
    public static ProgramAnalysis Analyze(
        IReadOnlyCollection<SourceFile> files, ProjectSettings project, Func<string, long?>? binaryLength = null) =>
        Analyze([.. files.Select(SyntaxTree.Parse)], project, binaryLength);

    /// <summary>
    /// Analyzes already-parsed files as one program. An editor keeps its trees across edits and
    /// reparses only what changed, so this overload takes the trees rather than their text.
    /// </summary>
    /// <param name="files">The program's files.</param>
    /// <param name="project">The settings the program is built with.</param>
    /// <param name="binaryLength">
    /// The function that returns the length of each file an <c>.incbin</c> names, or null to read
    /// the length from disk. Tests pass their own function so that they need not write files to
    /// disk.
    /// </param>
    public static ProgramAnalysis Analyze(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?>? binaryLength = null) =>
        Analyze(files, project, binaryLength, previous: null);

    /// <summary>
    /// Analyzes already-parsed files as one program, reusing <paramref name="previous"/> when it
    /// is not null. When a file changed and what the other files can see of it did not, only that
    /// file is analyzed again and everything else is kept. Otherwise the whole program is analyzed
    /// again.
    /// </summary>
    /// <param name="files">The program's files.</param>
    /// <param name="project">The settings the program is built with.</param>
    /// <param name="binaryLength">
    /// The function that returns the length of each file an <c>.incbin</c> names, or null to read
    /// the length from disk.
    /// </param>
    /// <param name="previous">The analysis from before an edit, or null to analyze from scratch.</param>
    /// <param name="cancellation">
    /// The token checked between files and between the stages of the analysis. A cancelled
    /// analysis throws <see cref="OperationCanceledException"/>.
    /// </param>
    public static ProgramAnalysis Analyze(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?>? binaryLength,
        ProgramAnalysis? previous, CancellationToken cancellation = default) =>
        Analyze(files, project, binaryLength, previous, settle: true, cancellation);

    /// <summary>
    /// Analyzes already-parsed files as one program, as
    /// <see cref="Analyze(IReadOnlyCollection{SyntaxTree}, ProjectSettings, Func{string, long?}?, ProgramAnalysis?, CancellationToken)"/>
    /// does, but leaves the program-wide answers for later where only some files are analyzed
    /// again. An editor calls it so that an edit is answered at once, and calls
    /// <see cref="Settle"/> once typing stops. An analysis of the whole program always works the
    /// answers out, because there are none from before to carry over. See
    /// <see cref="ProgramAnalysis.IsSettled"/>.
    /// </summary>
    /// <param name="files">The program's files.</param>
    /// <param name="project">The settings the program is built with.</param>
    /// <param name="binaryLength">
    /// The function that returns the length of each file an <c>.incbin</c> names, or null to read
    /// the length from disk.
    /// </param>
    /// <param name="previous">The analysis from before an edit, or null to analyze from scratch.</param>
    /// <param name="cancellation">
    /// The token checked between files and between the stages of the analysis. A cancelled
    /// analysis throws <see cref="OperationCanceledException"/>.
    /// </param>
    public static ProgramAnalysis AnalyzeUnsettled(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?>? binaryLength,
        ProgramAnalysis? previous, CancellationToken cancellation = default) =>
        Analyze(files, project, binaryLength, previous, settle: false, cancellation);

    /// <summary>
    /// Analyzes already-parsed files as one program, reusing <paramref name="previous"/>, and
    /// works out the program-wide answers only where <paramref name="settle"/> is true.
    /// </summary>
    private static ProgramAnalysis Analyze(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?>? binaryLength,
        ProgramAnalysis? previous, bool settle, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        binaryLength ??= BinaryLengthOnDisk;
        (Configuration, List<Diagnostic>)? resolved = null;
        var reason = previous is null
            ? WholeProgramReason.NoPreviousAnalysis
            : ReasonForWholeProgram(previous, files, project, binaryLength, out resolved);
        if (reason is null && Reanalyze(previous!, files, project, binaryLength, resolved, settle, cancellation) is { } reused)
            return reused;

        // An analysis of the changed files that finds a diagnostic in text an edit replaced
        // cannot tell where that diagnostic belongs now.
        return AnalyzeAll(files, project, binaryLength, previous is { Files: [var first, ..] } ? first.Flow.Signatures : null, cancellation) with
        {
            WholeProgram = reason ?? WholeProgramReason.DiagnosticInEditedText,
        };
    }

    /// <summary>
    /// Returns <paramref name="analysis"/> with the program-wide answers worked out, or the
    /// analysis itself when they already are. The analysis given is left as it is, so that a
    /// request still reading it is not disturbed.
    /// </summary>
    /// <param name="analysis">An analysis, settled or not.</param>
    /// <param name="cancellation">The token checked before each round of working the answers out.</param>
    public static ProgramAnalysis Settle(ProgramAnalysis analysis, CancellationToken cancellation = default)
    {
        if (analysis.IsSettled || analysis.Reused is not { } reuse)
            return analysis;
        cancellation.ThrowIfCancellationRequested();
        var cpu = new List<Diagnostic>();
        ProgramCpu.Resolve(reuse.Trees, reuse.Project.Cpu, cpu);

        // The answers are set on copies of the flows, which the analysis given keeps its own of.
        var files = analysis.Files.Select(file => file with { Flow = file.Flow.ForComposing() }).ToList();
        var copy = new ProgramAnalysis(analysis.Program, analysis.Cpu, files, analysis.Configuration, [])
        {
            Reanalyzed = analysis.Reanalyzed,
            WholeProgram = analysis.WholeProgram,
        };
        return Composed(copy, reuse.Project, cpu, reuse, carried: null, cancellation);
    }

    /// <summary>
    /// Emits one file of the program. <paramref name="measured"/> holds, for each file, the
    /// symbols it declares that other files measure with <c>.endof</c> and <c>.spanof</c>, which
    /// are the ones its output has to label.
    /// </summary>
    private static OutputFile EmitFile(
        ProgramAnalysis analysis, ProjectSettings project, FileAnalysis file,
        IReadOnlyDictionary<SyntaxTree, IReadOnlySet<Symbol>> measured, List<Diagnostic> diagnostics)
    {
        // The analysis has reported every name that collides in the output, so building the
        // tables again reports nothing new.
        var members = OutputNames.Tables(analysis, analysis.Placements, file, measured, []);
        if (analysis.Placements.UnitOf(file.Model.Tree) is not { IsPlaced: true })
        {
            var (model, layout, names, elsewhere) = members[0];
            return Emitter.Emit(model, layout, names, diagnostics, project.Out, elsewhere);
        }
        return Emitter.Emit(members, analysis.Placements, diagnostics, project.Out);
    }

    /// <summary>
    /// Analyzes every file of the program. Each file's analysis starts from
    /// <paramref name="signatures"/>, the signatures an earlier analysis inferred, where there is
    /// one, so that a file whose routines' signatures are the same is not analyzed twice.
    /// </summary>
    private static ProgramAnalysis AnalyzeAll(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?> binaryLength,
        Flow.InferredSignatures? signatures, CancellationToken cancellation)
    {
        // The modules that come with nt65 are added to a program that could name them. A caller
        // that passes back the files of an earlier analysis passes those modules too, so they are
        // removed from the caller's files first.
        var trees = files.Where(tree => !StandardModules.IsStandard(tree.Path))
            .OrderBy(tree => tree.Path, StringComparer.Ordinal).ToList();
        if (StandardModules.Wanted(trees))
            trees.AddRange(StandardModules.Trees);

        // The length taken for each `.incbin` file is kept, so that a later edit can tell whether
        // the file has changed on disk since.
        var lengths = new ConcurrentDictionary<string, long?>(StringComparer.Ordinal);
        long? Length(string path) => lengths.GetOrAdd(path, binaryLength);

        // The segment table and the CPU are the program's: a segment is declared exactly once
        // across it, and it is built for one processor.
        var cpu = new List<Diagnostic>();
        var target = ProgramCpu.Resolve(trees, project.Cpu, cpu);

        // Which `.if` branches the build takes is decided first. Conditions test the
        // configuration and nothing else, and which segments and declarations a program has
        // follows from the answers.
        var conditions = new List<Diagnostic>();
        var configuration = Configuration.Resolve(trees, target, project.SettingValues, conditions);
        var segmentTable = new List<Diagnostic>();
        var segments = SegmentTable.Build(
            trees, project.Segments, project.Spaces, project.Links.Count > 0, configuration, segmentTable);
        cancellation.ThrowIfCancellationRequested();

        // Every file is read before any is resolved, because a name one file uses may be one
        // another file exports.
        var program = ProgramModel.Create(trees, segments, configuration, Length, target);
        cancellation.ThrowIfCancellationRequested();
        var analyzed = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        var analyses = AnalyzeFiles(program, target, project, analyzed, previous: null, dirty: null, signatures, cancellation);
        var reuse = new ProgramAnalysis.Reuse(
            project, trees, ByFile(trees, conditions), analyzed, segmentTable, lengths);
        return Composed(
            new ProgramAnalysis(program, target, analyses, configuration, [])
            {
                Reanalyzed = program.Files.Count,
            },
            project, cpu, reuse, carried: null, cancellation);
    }

    /// <summary>
    /// Returns the reason the program that <paramref name="previous"/> analyzed must be analyzed
    /// again as a whole, now that its files are <paramref name="files"/>, or null when the files
    /// that changed can be analyzed on their own. The whole program is analyzed again when
    /// anything decided for it as a whole changes, such as the files in it, the project, the CPU
    /// or the segments. Where deciding needed the configuration of the files as they now stand,
    /// <paramref name="resolved"/> receives it with the diagnostics found resolving it, and
    /// otherwise it receives null.
    /// </summary>
    private static WholeProgramReason? ReasonForWholeProgram(
        ProgramAnalysis previous, IReadOnlyCollection<SyntaxTree> files, ProjectSettings project,
        Func<string, long?> binaryLength, out (Configuration Configuration, List<Diagnostic> Found)? resolved)
    {
        resolved = null;
        if (previous.Reused is not { } reuse || reuse.Project != project)
            return WholeProgramReason.ProjectChanged;
        var sources = Sources(files);
        var earlier = Earlier(previous, reuse);
        if (sources.Count != earlier.Count || earlier.Any(tree => !sources.ContainsKey(tree.Path)))
            return WholeProgramReason.FilesAddedOrRemoved;

        // An edit that first mentions nt65's own modules, or takes away the last mention, adds
        // them to the program or takes them out.
        if (StandardModules.Wanted(sources.Values) != reuse.Trees.Any(tree => StandardModules.IsStandard(tree.Path)))
            return WholeProgramReason.FilesAddedOrRemoved;

        // An `.incbin` file that changed on disk changes every file that includes it, whether or
        // not any source changed with it.
        if (reuse.Lengths.Any(pair => binaryLength(pair.Key) != pair.Value))
            return WholeProgramReason.BinaryFileChanged;
        var changed = earlier.Where(tree => sources[tree.Path] != tree).ToList();
        if (changed.Count == 0)
            return null;
        if (changed.Any(tree => SegmentTable.Declares(tree) || SegmentTable.Declares(sources[tree.Path])))
            return WholeProgramReason.SegmentsDeclared;
        if (ProgramCpu.Resolve(Current(reuse, sources), project.Cpu, []) != previous.Cpu)
            return WholeProgramReason.CpuChanged;

        // A condition in one file may test a value another file declares, so an edit can change
        // which branches a file it does not touch takes, or what is wrong with its conditions.
        var found = new List<Diagnostic>();
        var trees = Current(reuse, sources);
        var fresh = Configuration.Resolve(trees, previous.Cpu, project.SettingValues, found);
        resolved = (fresh, found);
        var byFile = ByFile(trees, found);
        foreach (var tree in earlier.Where(tree => sources[tree.Path] == tree))
        {
            if (!fresh.AnswersAlike(previous.Configuration, tree)
                || !byFile[tree.Path].Select(Spelled).SequenceEqual(reuse.Conditions[tree.Path].Select(Spelled)))
            {
                return WholeProgramReason.ConditionsChanged;
            }
        }
        return null;

        static string Spelled(Diagnostic diagnostic) => $"{diagnostic.Id} {diagnostic.Message}";
    }

    /// <summary>
    /// Reanalyzes the program that <paramref name="previous"/> analyzed after some of its files
    /// changed, analyzing only those files and the files the changes affect. Returns null when a
    /// diagnostic from before points at text an edit replaced, and the whole program has to be
    /// analyzed again. <see cref="ReasonForWholeProgram"/> must already have found no reason to
    /// analyze the whole program. <paramref name="resolved"/> is the configuration that it
    /// resolved for the files as they now stand, with its diagnostics, or null to resolve it here.
    /// </summary>
    private static ProgramAnalysis? Reanalyze(
        ProgramAnalysis previous, IReadOnlyCollection<SyntaxTree> files, ProjectSettings project,
        Func<string, long?> binaryLength, (Configuration Configuration, List<Diagnostic> Found)? resolved, bool settle,
        CancellationToken cancellation)
    {
        var reuse = previous.Reused!;
        var sources = Sources(files);
        var changed = Earlier(previous, reuse).Where(tree => sources[tree.Path] != tree).ToList();
        if (changed.Count == 0)
            return previous;
        var lengths = new ConcurrentDictionary<string, long?>(reuse.Lengths, StringComparer.Ordinal);
        long? Length(string path) => lengths.GetOrAdd(path, binaryLength);
        var trees = Current(reuse, sources);
        var cpu = new List<Diagnostic>();
        ProgramCpu.Resolve(trees, project.Cpu, cpu);

        // The conditions of every other file are answered as they were, which is why only the
        // changed files are analyzed again, so only theirs are kept from this pass.
        var reported = resolved?.Found ?? [];
        var configuration = resolved?.Configuration
            ?? Configuration.Resolve(trees, previous.Cpu, project.SettingValues, reported);
        var byFile = ByFile(trees, reported);
        var conditions = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        foreach (var before in changed)
            conditions[before.Path] = byFile[before.Path];

        // What is wrong with the values the build gives its settings is in no source file, and an
        // edit to any file can change it, so it is always taken from this pass.
        var sourcePaths = trees.Select(tree => tree.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, found) in byFile.Where(pair => !sourcePaths.Contains(pair.Key)))
            conditions[path] = found;
        var moved = EditMap.Composed([.. changed.Select(before => new EditMap(before, sources[before.Path]))]);

        // The set of files to analyze again starts as the ones that changed, and grows by every
        // file that analysis finds a change affects, until a pass adds no more files.
        var current = trees.ToDictionary(tree => tree.Path, StringComparer.Ordinal);
        var dirty = changed.Select(tree => tree.Path).ToHashSet(StringComparer.Ordinal);
        var analyzed = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        ProgramModel? program;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            program = previous.Program.Reanalyzing(
                current, dirty, configuration, moved, Length, out var affected);
            if (program is null && affected.Count == 0)
                return null;

            // The diagnostics from laying out every other file still hold, moved to follow the
            // edit; a file whose diagnostics cannot be moved is analyzed again.
            analyzed.Clear();
            foreach (var (path, found) in reuse.Analyzed.Where(pair => !dirty.Contains(pair.Key)))
            {
                if (EditMap.Moved(found, moved) is { } kept)
                    analyzed[path] = kept;
                else
                    affected.Add(path);
            }
            if (program is not null && affected.Count == 0)
                break;
            dirty.UnionWith(affected);
        }

        foreach (var (path, found) in reuse.Conditions.Where(pair => sourcePaths.Contains(pair.Key) && !conditions.ContainsKey(pair.Key)))
        {
            if (EditMap.Moved(found, moved) is not { } kept)
                return null;
            conditions[path] = kept;
        }
        if (EditMap.Moved(reuse.SegmentTable, moved) is not { } segmentTable)
            return null;

        var analyses = AnalyzeFiles(program, previous.Cpu, project, analyzed, previous, dirty, null, cancellation);
        return Composed(
            new ProgramAnalysis(program, previous.Cpu, analyses, configuration, [])
            {
                Reanalyzed = dirty.Count,
            },
            project, cpu, new ProgramAnalysis.Reuse(project, trees, conditions, analyzed, segmentTable, lengths),
            settle ? null : Carried(previous, reuse, moved), cancellation);
    }

    /// <summary>
    /// Returns the files of a program as the caller gives them, by path, without the modules
    /// that come with nt65.
    /// </summary>
    private static Dictionary<string, SyntaxTree> Sources(IReadOnlyCollection<SyntaxTree> files) =>
        files.Where(tree => !StandardModules.IsStandard(tree.Path)).ToDictionary(tree => tree.Path, StringComparer.Ordinal);

    /// <summary>
    /// Returns the files that <paramref name="previous"/> analyzed as the caller gave them,
    /// without the modules that come with nt65.
    /// </summary>
    private static List<SyntaxTree> Earlier(ProgramAnalysis previous, ProgramAnalysis.Reuse reuse) =>
        [.. reuse.Trees.Where(tree => !StandardModules.IsStandard(tree.Path))];

    /// <summary>
    /// Returns every file of the program that <paramref name="reuse"/> kept, with each file the
    /// caller gives replaced by its current version.
    /// </summary>
    private static List<SyntaxTree> Current(ProgramAnalysis.Reuse reuse, Dictionary<string, SyntaxTree> sources) =>
        [.. reuse.Trees.Select(tree => sources.GetValueOrDefault(tree.Path) ?? tree)];

    /// <summary>
    /// Analyzes each file of <paramref name="program"/> on its own, and returns what each file's
    /// analysis found, in the program's order. When <paramref name="previous"/> is given, only the
    /// files at <paramref name="dirty"/> are analyzed, and every other file keeps what
    /// <paramref name="previous"/> found for it. <paramref name="analyzed"/> receives the
    /// diagnostics of each file analyzed. Each file analyzed takes <paramref name="signatures"/>
    /// for the routines it reaches, or else what <paramref name="previous"/> inferred.
    /// <paramref name="cancellation"/> is checked before each file, and after the last, so that a
    /// cancelled analysis stops before the program is composed.
    /// </summary>
    private static List<FileAnalysis> AnalyzeFiles(
        ProgramModel program, Cpu target, ProjectSettings project, Dictionary<string, IReadOnlyList<Diagnostic>> analyzed,
        ProgramAnalysis? previous, IReadOnlySet<string>? dirty, Flow.InferredSignatures? signatures,
        CancellationToken cancellation)
    {
        // Each file is analyzed on its own, so the files are analyzed at once, and what they
        // found is gathered in the program's order.
        var models = program.Files;
        var files = new FileAnalysis[models.Count];
        var found = new IReadOnlyList<Diagnostic>?[models.Count];
        ParallelWork.For(models.Count, i =>
        {
            // A file kept from before keeps what was found for it then, under the program's
            // current model of the file. Its flow is copied, because composing this analysis sets
            // program-wide answers on the routines, and the earlier analysis keeps its own.
            var model = models[i];
            if (dirty?.Contains(model.Tree.Path) != true && previous?.FileFor(model.Tree.Path) is { } kept)
            {
                files[i] = kept with { Model = model, Flow = kept.Flow.ForComposing() };
                return;
            }

            // A file analyzed again starts from the stack effects the earlier analysis found, which
            // are what an edit to one file usually leaves them.
            var (layout, flow, state, diagnostics) = AnalyzeFile(
                model, target, project,
                previous is { Files: [var any, ..] } ? any.Flow.Effects : Flow.StackEffects.None,
                previous is { Files: [var some, ..] } ? some.Flow.FlagExits : Flow.FlagExits.None,
                signatures ?? (previous is { Files: [var first, ..] } ? first.Flow.Signatures : Flow.InferredSignatures.None));
            files[i] = new FileAnalysis(model, layout, flow, state);
            found[i] = diagnostics;
        }, cancellation);
        for (var i = 0; i < models.Count; i++)
        {
            if (found[i] is { } diagnostics)
                analyzed[models[i].Tree.Path] = diagnostics;
        }

        // Composing the program sets its answers on this analysis's regions, which for a file kept
        // from before are the copies made above, so the previous analysis keeps its own answers.
        // Composing cannot be cancelled once it starts, so a cancelled analysis stops here.
        cancellation.ThrowIfCancellationRequested();
        return [.. files];
    }

    /// <summary>
    /// Returns <paramref name="analysis"/>, whose files have each been analyzed on their own,
    /// completed with what only the whole program can answer, and with every diagnostic
    /// collected. Both a whole program and a program in which some files changed are completed
    /// by this method, so the two report the same diagnostics. Where <paramref name="carried"/>
    /// is given, the program-wide answers are not worked out, and its diagnostics and the routines
    /// it says read their caller's stack stand in for them until the analysis is settled.
    /// </summary>
    private static ProgramAnalysis Composed(
        ProgramAnalysis analysis, ProjectSettings project, IReadOnlyList<Diagnostic> cpu, ProgramAnalysis.Reuse reuse,
        (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlySet<Flow.RoutineKey> Readers)? carried,
        CancellationToken cancellation)
    {
        var program = analysis.Program;
        if (carried is var (carriedDiagnostics, carriedReaders))
        {
            return Completed(analysis with { IsSettled = false }, project, cpu, reuse with { Composed = carriedDiagnostics },
                carriedDiagnostics, carriedReaders);
        }

        // What a routine costs including its calls, which registers it preserves for its caller
        // and reads from it, and what it leaves on its caller's stack, are questions about the
        // program rather than about one file, so they are worked out once every file has been
        // analyzed on its own. A file kept from before an edit keeps its own costs, but its
        // routines' costs including their calls, and the registers they preserve, may still have
        // changed, because a routine they call may be in a file that changed.
        //
        // The 65816's processor-state analysis runs on each file alone, and takes for each
        // routine a call reaches the stack effect it was given. A file that took an effect the
        // program turns out to differ on is analyzed again with the program's, and the program
        // composed again, until no file took a stale one. Each round only moves effects towards
        // unknown, but the rounds are capped as well, so a cycle the analysis does not foresee
        // cannot run on.
        //
        // The flag analysis of each file likewise takes what each routine it calls returns with in
        // the flags. Those answers are worked out once every routine's registers are settled, and
        // a file whose decisions or checks took an answer the program differs on is analyzed again.
        //
        // So does each file's control flow take which routines never return, and its
        // processor-state analysis the signature of each routine, inferred where the routine does
        // not declare it. The states are solved over the files' existing layouts before any file
        // is laid out again, so a file is laid out again once for them, however long the chains
        // of calls they pass along.
        IReadOnlyList<Diagnostic> registers;
        IReadOnlySet<Flow.RoutineKey> readers;
        for (var round = 0; ; round++)
        {
            Flow.CallCosts.Compose(analysis.Files.Select(file => file.Flow));
            (registers, readers, var effects) = Flow.RegisterKeeps.Compose(analysis.Files);
            var exits = Flow.FlagExits.Solve(analysis.Files);
            var signatures = Flow.InferredSignatures.Solve(analysis.Files, effects, project.Ranges, cancellation);
            foreach (var file in analysis.Files)
            {
                file.Flow.FlagExits = exits;
                file.Flow.Signatures = signatures;
            }
            var stale = analysis.Files
                .Where(file => file.State?.Consumed.Any(taken => effects.Of(taken.Key) != taken.Value) == true
                    || file.Flow.Flags?.Consumed.Any(taken => taken.IsStale(exits)) == true
                    || file.State?.TookStale(signatures) == true
                    || file.Flow.NeverReturnsTaken.Any(taken => signatures.Of(taken.Key)?.NeverReturns != taken.Value))
                .ToHashSet();
            if (stale.Count == 0 || round == StaleRounds)
                break;
            cancellation.ThrowIfCancellationRequested();
            var analyzed = reuse.Analyzed.ToDictionary(StringComparer.Ordinal);
            var files = analysis.Files.ToArray();
            var found = new IReadOnlyList<Diagnostic>?[files.Length];
            ParallelWork.For(files.Length, i =>
            {
                if (!stale.Contains(files[i]))
                    return;
                var (layout, flow, state, diagnostics) = AnalyzeFile(files[i].Model, analysis.Cpu, project, effects, exits, signatures);
                files[i] = new FileAnalysis(files[i].Model, layout, flow, state);
                found[i] = diagnostics;
            }, cancellation);
            for (var i = 0; i < files.Length; i++)
            {
                if (found[i] is { } diagnostics)
                    analyzed[files[i].Path] = diagnostics;
            }
            analysis = new ProgramAnalysis(program, analysis.Cpu, files, analysis.Configuration, [])
            {
                Reanalyzed = analysis.Reanalyzed + stale.Count,
            };
            reuse = reuse with { Analyzed = analyzed };
        }

        return Completed(analysis with { IsSettled = true }, project, cpu, reuse with { Composed = registers }, registers, readers);
    }

    /// <summary>
    /// Returns <paramref name="analysis"/> with the checks of the translation units made and every
    /// diagnostic collected. <paramref name="composed"/> holds the diagnostics found by working
    /// out the program-wide answers, and <paramref name="readers"/> the routines those answers
    /// say read their caller's stack.
    /// </summary>
    private static ProgramAnalysis Completed(
        ProgramAnalysis analysis, ProjectSettings project, IReadOnlyList<Diagnostic> cpu, ProgramAnalysis.Reuse reuse,
        IReadOnlyList<Diagnostic> composed, IReadOnlySet<Flow.RoutineKey> readers)
    {
        // Which modules place which others follows from the files alone. Which routine a routine
        // falls through into across a `.place` follows from the layouts of every file in its
        // translation unit, so an edit to any module of a translation unit can change it for the
        // others, and the placements are worked out again after any change.
        var program = analysis.Program;
        var placements = Placements.Of(reuse.Trees);
        return analysis with
        {
            Diagnostics = Collected(project, analysis.Cpu, cpu, program, reuse, [
                .. composed, .. placements.Diagnostics,
                .. Flow.RunningOnChecks.Check(program, analysis.Files, placements),
                .. OutputNames.Collisions(analysis, placements)]),
            Reused = reuse,
            Placements = placements,
            CallerStackReaders = readers,
        };
    }

    /// <summary>
    /// Returns what an analysis that leaves the program-wide answers for later carries over from
    /// <paramref name="previous"/>. Its program-wide diagnostics are moved to follow the edit,
    /// and one in text the edit replaced is dropped, since only working the answers out again can
    /// say whether it still holds.
    /// </summary>
    private static (IReadOnlyList<Diagnostic>, IReadOnlySet<Flow.RoutineKey>) Carried(
        ProgramAnalysis previous, ProgramAnalysis.Reuse reuse, Func<Diagnostic, Diagnostic?> moved) =>
        ([.. reuse.Composed.Select(moved).OfType<Diagnostic>()], previous.CallerStackReaders);

    /// <summary>
    /// Analyzes one file once its names are resolved. Returns the file's layout, its control flow
    /// and, on the 65816, its processor state, together with the diagnostics they found.
    /// </summary>
    private static (CodeLayout Layout, Flow.ControlFlow Flow, Flow.StateAnalysis? State, IReadOnlyList<Diagnostic> Found)
        AnalyzeFile(
            SemanticModel model, Cpu target, ProjectSettings project, Flow.StackEffects effects, Flow.FlagExits exits,
            Flow.InferredSignatures signatures)
    {
        // Control flow is read from the order in which layout lays out the bytes, so the macros
        // are expanded and the repetitions unrolled before anything is asked about the path.
        var layout = CodeLayout.Create(model, target);
        var flow = Flow.ControlFlow.Of(model, layout, exits, signatures);
        var found = new List<Diagnostic>();

        // On the 65816 an immediate is as wide as the register it goes to, and the
        // processor-state analysis determines that width. No edge depends on a length, so the
        // analysis runs over the first layout, and the file is laid out again with its results.
        Flow.StateAnalysis? state = null;
        if (target == Cpu.Wdc65816)
        {
            state = Flow.StateAnalysis.Of(model, layout, flow, project.Ranges, effects, signatures);
            found.AddRange(state.Diagnostics);
            layout = CodeLayout.Create(model, target, state);
            flow = Flow.ControlFlow.Of(model, layout, exits, signatures);
        }

        // An `.ensure` that names a flag emits nothing where the flags already hold, which the
        // flag analysis of the layout so far tells. What it emits changes no edge and no flag, so
        // one more layout settles it. Only a file with such an `.ensure` pays for it.
        if (layout.Steps.Any(step => step.Statement is EnsureDirectiveSyntax ensure
            && StateItem.Read(ensure).Any(item => item.Part == StatePart.Flag)))
        {
            layout = CodeLayout.Create(model, target, state, flow.Flags!.Known);
            flow = Flow.ControlFlow.Of(model, layout, exits, signatures);
        }
        found.AddRange(layout.Diagnostics);
        found.AddRange(flow.Diagnostics);

        // A family's body is emitted once per instance, so a mistake in it is found once per
        // instance. A diagnostic that every instance reports is collapsed into one.
        var collapsed = new List<Diagnostic>(Family.Collapsed(model.Families, found));
        var entries = flow.Regions.SelectMany(region => region.Blocks)
            .Where(block => block.IsDeclared)
            .Select(block => block.Label!);
        collapsed.AddRange(UnusedSymbols.Of(model, collapsed, entries).ToList());
        return (layout, flow, state, collapsed);
    }

    /// <summary>
    /// Returns every diagnostic for the program, collected from what each part of the analysis
    /// found. <paramref name="composed"/> holds the diagnostics found after every file had been
    /// analyzed on its own, which come from working out which registers routines preserve across
    /// calls and from the translation-unit checks.
    /// </summary>
    private static IReadOnlyList<Diagnostic> Collected(
        ProjectSettings project, Cpu target, IReadOnlyList<Diagnostic> cpu, ProgramModel program,
        ProgramAnalysis.Reuse reuse, IReadOnlyList<Diagnostic> composed)
    {
        var diagnostics = new List<Diagnostic>(project.Diagnostics);
        diagnostics.AddRange(cpu);
        diagnostics.AddRange(composed);
        diagnostics.AddRange(reuse.Conditions.Values.SelectMany(found => found));
        diagnostics.AddRange(reuse.SegmentTable);
        diagnostics.AddRange(reuse.Trees.SelectMany(tree => tree.Diagnostics));
        diagnostics.AddRange(program.Diagnostics);
        diagnostics.AddRange(reuse.Analyzed.Values.SelectMany(found => found));
        if (target != Cpu.Wdc65816)
            diagnostics.AddRange(FarNeedsA65816(program.Segments, program));

        // An `.allow` hides the warning it names before the project's severities apply, so a
        // warning the project raises to an error can still be allowed where code relies on it.
        var allowances = Allowances.Of(program);
        var used = new HashSet<Allowance>();
        var kept = allowances.Apply(diagnostics, used).ToList();
        kept.AddRange(allowances.Unused(used));
        return Diagnostics.Ordered(Diagnostics.WithSeverities(kept, project.Severities));
    }

    /// <summary>
    /// Groups diagnostics by the file they are in. A diagnostic about something the build gave,
    /// which is in no file, is grouped under an empty path.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<Diagnostic>> ByFile(
        IEnumerable<SyntaxTree> trees, IEnumerable<Diagnostic> diagnostics)
    {
        var found = trees.ToDictionary(tree => tree.Path, _ => new List<Diagnostic>(), StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics)
        {
            var file = diagnostic.Span.File ?? "";
            if (!found.TryGetValue(file, out var list))
                found[file] = list = [];
            list.Add(diagnostic);
        }
        return found.ToDictionary(
            pair => pair.Key, IReadOnlyList<Diagnostic> (pair) => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the logical paths of the files that a file's output depends on.
    /// <list type="bullet">
    /// <item><description>The file's own source.</description></item>
    /// <item><description>
    /// The sources of the modules whose interfaces it uses, and of the modules those use in turn.
    /// </description></item>
    /// <item><description>
    /// The files that declare segments, and those whose values the configuration decides, because
    /// any file's meaning may follow from them.
    /// </description></item>
    /// <item><description>
    /// The files an <c>.incbin</c> in any of those sources names, because their lengths determine
    /// the addresses that follow.
    /// </description></item>
    /// </list>
    /// <paramref name="direct"/> caches the files each file names directly, for later calls to reuse.
    /// </summary>
    private static IReadOnlyList<string> Dependencies(
        ProgramModel program, SemanticModel model, Dictionary<SyntaxTree, HashSet<string>> direct)
    {
        var models = program.Files.ToDictionary(file => file.Tree.Path, StringComparer.Ordinal);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<SemanticModel>([model]);
        var seen = new HashSet<SyntaxTree> { model.Tree };
        while (pending.TryPop(out var file))
        {
            found.Add(file.Tree.Path);
            foreach (var path in Named(file))
            {
                if (!models.TryGetValue(path, out var other))
                    found.Add(path);
                else if (seen.Add(other.Tree))
                    pending.Push(other);
            }
        }
        foreach (var file in program.Files)
        {
            if (SegmentTable.Declares(file.Tree) || model.Configuration.Decides(file.Tree))
                found.Add(file.Tree.Path);
        }
        found.RemoveWhere(StandardModules.IsStandard);
        return [.. found];

        // Returns the other sources a file names, and the binaries it includes.
        HashSet<string> Named(SemanticModel file)
        {
            if (direct.TryGetValue(file.Tree, out var named))
                return named;
            named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in file.References)
                named.Add(reference.Symbol.Tree.Path);
            foreach (var used in file.Used)
                named.Add(used.Tree.Path);
            foreach (var directive in file.Tree.Root.DescendantNodes().OfType<DataDirectiveSyntax>())
            {
                if (directive.Directive.DirectiveKind == DirectiveKind.IncBin
                    && directive.Tail is InlineDataSyntax { Values: [var operand, ..] }
                    && file.ValueOf(operand) is { Kind: ValueKind.String, Text: { } included })
                {
                    named.Add(Paths.Beside(file.Tree.Path, included));
                }
            }
            named.Remove(file.Tree.Path);
            return direct[file.Tree] = named;
        }
    }

    /// <summary>
    /// Returns the length of the file at <paramref name="path"/>, or null when it cannot be read.
    /// </summary>
    private static long? BinaryLengthOnDisk(string path)
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
    /// Reports a diagnostic for each segment or import declared far on a 6502 or a CMOS variant.
    /// A far address is a bank and an offset, which only the 65816 has. ca65 refuses <c>far</c>
    /// on any earlier processor, and nt65 output that ca65 refuses is an nt65 bug. Such a
    /// declaration is therefore reported where it is declared, rather than emitted for ca65 to
    /// reject.
    /// </summary>
    private static IEnumerable<Diagnostic> FarNeedsA65816(SegmentTable segments, ProgramModel program)
    {
        foreach (var segment in segments.Segments)
        {
            if (segment is { Size: AddressSize.Far, Declaration: { } declared })
                yield return new Diagnostic(declared, Catalogue.FarNeeds65816.Message($"segment \"{segment.Name}\""));
        }
        foreach (var symbol in program.Files.SelectMany(file => file.Symbols))
        {
            if (symbol is { Kind: SymbolKind.ImportedAddress, AddressSize: AddressSize.Far })
                yield return new Diagnostic(symbol.DeclarationSpan, Catalogue.FarNeeds65816.Message($"`{symbol.Name}`"));
        }
    }
}
