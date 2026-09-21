using System.Collections.Concurrent;
using Norristown.Emit;
using Norristown.Layout;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown;

/// <summary>
/// The whole pipeline, from a set of source files to ca65 output and diagnostics. The
/// order of <c>files</c> must not affect the result.
/// </summary>
public static class Compiler
{
    /// <summary>Compiles <paramref name="files"/> as one program, with no project file.</summary>
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files) =>
        Compile(files, ProjectSettings.None);

    /// <summary>
    /// Compiles <paramref name="files"/> for <paramref name="cpu"/>, which is what the
    /// command line says if it says anything; a <c>.cpu</c> item must agree with it.
    /// </summary>
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files, Cpu? cpu) =>
        Compile(files, ProjectSettings.None with { Cpu = cpu });

    /// <summary>Compiles <paramref name="files"/> as the program <paramref name="project"/> describes.</summary>
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files, ProjectSettings project) =>
        Compile(files, project, BinaryLengthOnDisk);

    /// <summary>
    /// The same, with <paramref name="binaryLength"/> answering how long the file an
    /// <c>.incbin</c> names is, for a caller whose files are not where the paths say, and a C
    /// header of what the program exports when <paramref name="cHeader"/> names one.
    /// </summary>
    public static Compilation Compile(
        IReadOnlyCollection<SourceFile> files, ProjectSettings project, Func<string, long?> binaryLength,
        string? cHeader = null) =>
        Emit(Analyze([.. files.Select(SyntaxTree.Parse)], project, binaryLength), project, cHeader);

    /// <summary>
    /// Writes out the program <paramref name="analysis"/> worked out, and a C header of what it
    /// exports when <paramref name="cHeader"/> names the file it goes in.
    /// </summary>
    public static Compilation Emit(ProgramAnalysis analysis, ProjectSettings project, string? cHeader = null)
    {
        var diagnostics = new List<Diagnostic>(analysis.Diagnostics);
        var outputs = new List<OutputFile>();
        var direct = new Dictionary<SyntaxTree, HashSet<string>>();

        // Every file is written even when the program is already wrong, because what emission
        // finds — a construct no stage has reached, two names that meet in the output — is
        // worth reporting alongside the rest rather than only once the rest is fixed.
        var measured = analysis.Program.Files.Select(Extents.MeasuredIn).ToList();
        for (var i = 0; i < analysis.Layouts.Count; i++)
        {
            var model = analysis.Program.Files[i];

            // The defines are not a file anyone wrote, and nothing is written for them.
            if (model.Tree == analysis.Defines)
                continue;
            var output = Written(analysis, project, i, measured, diagnostics) with
            {
                Dependencies = Dependencies(analysis.Program, model, direct),
            };
            outputs.Add(output);

            // Where its lines came from goes beside it rather than into it, so that the ca65 is
            // only the program; `nt65 remap-dbg` puts it into ld65's debug file after the link.
            if (LineMap.For(output) is { } map)
                outputs.Add(map);
        }
        var header = cHeader is null ? null : CHeader.Write(analysis.Program, cHeader, diagnostics);

        // A program that is wrong produces no output: what would be written for it is not a
        // translation of anything. What counts as wrong is the project's to say by name, so
        // what it says about each is applied before anything reads the severities.
        var ordered = Diagnostics.Ordered(Diagnostics.WithSeverities(diagnostics, project.Severities));
        var wrong = ordered.Any(d => d.Severity == Severity.Error);
        return new Compilation(wrong ? [] : outputs, ordered)
        {
            Header = wrong ? null : header,
            IsCpuAssumed = project.Cpu is null && !ProgramCpu.IsStated(analysis.Program.Files.Select(file => file.Tree)),
        };
    }

    /// <summary>
    /// The ca65 for one file of <paramref name="analysis"/>, whatever is wrong with the rest of
    /// the program, or null when the program has no such file. A build writes nothing for a
    /// program that is wrong; the editor shows what would have been written anyway, so that
    /// seeing what a line became does not wait for the rest of the file to be right.
    /// </summary>
    /// <param name="analysis">The program the file belongs to.</param>
    /// <param name="project">The project it is built as, whose <c>out</c> names where the file goes.</param>
    /// <param name="path">The logical path of the source to write.</param>
    public static OutputFile? EmitFile(ProgramAnalysis analysis, ProjectSettings project, string path)
    {
        var measured = analysis.Program.Files.Select(Extents.MeasuredIn).ToList();
        for (var i = 0; i < analysis.Layouts.Count; i++)
        {
            if (analysis.Program.Files[i] is { Tree: var tree } && tree != analysis.Defines && tree.Path == path)
                return Written(analysis, project, i, measured, []);
        }
        return null;
    }

    /// <summary>
    /// Reads <paramref name="files"/> as one program and works out what it means, without
    /// writing anything. This is what an editor asks for, and what <see cref="Compile(IReadOnlyCollection{SourceFile}, ProjectSettings)"/>
    /// emits from.
    /// </summary>
    public static ProgramAnalysis Analyze(IReadOnlyCollection<SourceFile> files, ProjectSettings project) =>
        Analyze([.. files.Select(SyntaxTree.Parse)], project);

    /// <summary>
    /// The same, for files that are already parsed. An editor keeps its trees across edits
    /// and re-parses only what changed, so analysis takes them rather than their text.
    /// </summary>
    public static ProgramAnalysis Analyze(IReadOnlyCollection<SyntaxTree> files, ProjectSettings project) =>
        Analyze(files, project, BinaryLengthOnDisk);

    /// <summary>
    /// The same, with <paramref name="binaryLength"/> answering how long the file an
    /// <c>.incbin</c> names is. A test gives its own rather than writing files to disk.
    /// </summary>
    public static ProgramAnalysis Analyze(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?> binaryLength) =>
        Analyze(files, project, binaryLength, previous: null);

    /// <summary>
    /// The same, for a program <paramref name="previous"/> analyzed before an edit. When one
    /// file changed and what the other files can see of it did not, only that file is analyzed
    /// again and everything else is kept; otherwise the whole program is.
    /// </summary>
    public static ProgramAnalysis Analyze(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, ProgramAnalysis? previous) =>
        Analyze(files, project, BinaryLengthOnDisk, previous);

    /// <summary>The same, with <paramref name="binaryLength"/> answering how long an <c>.incbin</c> file is.</summary>
    public static ProgramAnalysis Analyze(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?> binaryLength,
        ProgramAnalysis? previous)
    {
        var reason = WholeProgramReason.NoPreviousAnalysis;
        if (previous is not null && Reanalyze(previous, files, project, binaryLength, out reason) is { } reused)
            return reused;
        return AnalyzeAll(files, project, binaryLength) with { WholeProgram = reason };
    }

    /// <summary>
    /// One file of the program written out. <paramref name="measured"/> is what each file of the
    /// program measures with <c>.endof</c> and <c>.spanof</c>, in the order the files are in:
    /// what the other files measure of this one is what it has to label.
    /// </summary>
    private static OutputFile Written(
        ProgramAnalysis analysis, ProjectSettings project, int i,
        IReadOnlyList<IReadOnlySet<Symbol>> measured, List<Diagnostic> diagnostics)
    {
        var model = analysis.Program.Files[i];
        var elsewhere = measured.Where((_, j) => j != i).SelectMany(set => set)
            .Where(symbol => symbol.Tree == model.Tree)
            .ToHashSet();
        return Emitter.Emit(
            model, analysis.Layouts[i], FlatNames.Create(model, analysis.Cpu, diagnostics), diagnostics,
            project.Out, elsewhere);
    }

    private static ProgramAnalysis AnalyzeAll(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?> binaryLength)
    {
        var trees = files.OrderBy(tree => tree.Path, StringComparer.Ordinal).ToList();

        // How long each `.incbin` file was taken to be is kept, so that a later edit can tell
        // whether one changed on disk since.
        var lengths = new ConcurrentDictionary<string, long?>(StringComparer.Ordinal);
        long? Length(string path) => lengths.GetOrAdd(path, binaryLength);

        // The build configuration is read as a file of constants, so that defines are ordinary
        // symbols to scoping and evaluation; only emission treats them differently.
        var defines = Defines.Source([.. project.Defines.Where(define => !define.IsSetting)]) is { } source
            ? SyntaxTree.Parse(source)
            : null;
        if (defines is not null)
            trees.Add(defines);

        // The segment table and the CPU are the program's: a segment is declared exactly once
        // across it, and it is built for one processor.
        var cpu = new List<Diagnostic>();
        var target = ProgramCpu.Resolve(trees, project.Cpu, cpu);

        // Which `.if` branches the build takes is settled first: conditions test the
        // configuration and nothing else, and which segments and declarations a program has
        // follows from the answers.
        var conditions = new List<Diagnostic>();
        var configuration = Configuration.Resolve(trees, target, project.Defines, conditions);
        var segmentTable = new List<Diagnostic>();
        var segments = SegmentTable.Build(trees, project.Segments, configuration, segmentTable);

        // Every file is read before any is resolved, because a name one file uses may be one
        // another file exports.
        var program = ProgramModel.Create(trees, segments, configuration, defines, Length, target);

        var layouts = new List<CodeLayout>();
        var flows = new List<Flow.ControlFlow>();
        var states = new List<Flow.StateAnalysis>();
        var analyzed = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        foreach (var model in program.Files)
        {
            var (layout, flow, state, found) = AnalyzeFile(model, target, project);
            layouts.Add(layout);
            flows.Add(flow);
            if (state is not null)
                states.Add(state);
            analyzed[model.Tree.Path] = found;
        }

        // What a routine costs with its calls, and which registers it hands back, are questions
        // about the program rather than about one file, so they are worked out once every
        // file's own answers are in.
        Flow.CallCosts.Compose(flows);
        var registers = Flow.RegisterKeeps.Compose(program.Files, layouts, flows, states);
        var reuse = new ProgramAnalysis.Reuse(
            project, trees, ByFile(trees, conditions), analyzed, segmentTable, lengths);
        return new ProgramAnalysis(
            program, target, layouts, flows, states, defines, configuration,
            Collected(project, target, cpu, program, reuse, registers))
        {
            Reused = reuse,
            Reanalyzed = program.Files.Count,
        };
    }

    /// <summary>
    /// The program <paramref name="previous"/> analyzed, with some files changed, analyzing only
    /// those files and the files the changes reach; or null, with the <paramref name="reason"/>,
    /// when the whole program has to be analyzed again. Anything decided for the program as a
    /// whole changing — the files in it, the project, the CPU, the segments — is such a change.
    /// </summary>
    private static ProgramAnalysis? Reanalyze(
        ProgramAnalysis previous, IReadOnlyCollection<SyntaxTree> files, ProjectSettings project,
        Func<string, long?> binaryLength, out WholeProgramReason reason)
    {
        reason = WholeProgramReason.ProjectChanged;
        if (previous.Reused is not { } reuse || reuse.Project != project)
            return null;
        reason = WholeProgramReason.FilesAddedOrRemoved;
        var sources = files.ToDictionary(tree => tree.Path, StringComparer.Ordinal);
        var written = reuse.Trees.Where(tree => tree != previous.Defines).ToList();
        if (sources.Count != written.Count || written.Any(tree => !sources.ContainsKey(tree.Path)))
            return null;

        // An `.incbin` file that changed on disk changes every file that includes it, whether or
        // not any source changed with it.
        reason = WholeProgramReason.BinaryFileChanged;
        if (reuse.Lengths.Any(pair => binaryLength(pair.Key) != pair.Value))
            return null;
        var changed = written.Where(tree => sources[tree.Path] != tree).ToList();
        if (changed.Count == 0)
            return previous;
        var lengths = new ConcurrentDictionary<string, long?>(reuse.Lengths, StringComparer.Ordinal);
        long? Length(string path) => lengths.GetOrAdd(path, binaryLength);

        reason = WholeProgramReason.SegmentsDeclared;
        if (changed.Any(tree => SegmentTable.Declares(tree) || SegmentTable.Declares(sources[tree.Path])))
            return null;
        reason = WholeProgramReason.SettingsDeclared;
        if (changed.Any(tree => Configuration.DeclaresSettings(tree) || Configuration.DeclaresSettings(sources[tree.Path])))
            return null;
        List<SyntaxTree> trees = [.. reuse.Trees.Select(tree => sources.GetValueOrDefault(tree.Path) ?? tree)];
        var cpu = new List<Diagnostic>();
        reason = WholeProgramReason.CpuChanged;
        if (ProgramCpu.Resolve(trees, project.Cpu, cpu) != previous.Cpu)
            return null;

        // A condition depends on nothing but its own file and the build.
        var configuration = previous.Configuration;
        var conditions = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        foreach (var before in changed)
        {
            var found = new List<Diagnostic>();
            configuration = configuration.Replacing(before, sources[before.Path], previous.Cpu, project.Defines, found);
            conditions[before.Path] = found;
        }
        var moved = EditMap.Composed([.. changed.Select(before => new EditMap(before, sources[before.Path]))]);

        // The files to read again start as the ones that changed, and grow by every file the
        // reading finds a change reaches, until reading them all reaches no further.
        var current = trees.ToDictionary(tree => tree.Path, StringComparer.Ordinal);
        var dirty = changed.Select(tree => tree.Path).ToHashSet(StringComparer.Ordinal);
        var analyzed = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        ProgramModel? program;
        while (true)
        {
            program = previous.Program.Reanalyzing(
                current, dirty, configuration, previous.Defines, moved, Length, out var affected);
            if (program is null && affected.Count == 0)
            {
                reason = WholeProgramReason.DiagnosticInEditedText;
                return null;
            }

            // What laying out every other file found stands, carried to where an edit moved it.
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

        reason = WholeProgramReason.DiagnosticInEditedText;
        foreach (var (path, found) in reuse.Conditions.Where(pair => !conditions.ContainsKey(pair.Key)))
        {
            if (EditMap.Moved(found, moved) is not { } kept)
                return null;
            conditions[path] = kept;
        }
        if (EditMap.Moved(reuse.SegmentTable, moved) is not { } segmentTable)
            return null;

        List<CodeLayout> layouts = [.. previous.Layouts];
        List<Flow.ControlFlow> flows = [.. previous.Flows];
        List<Flow.StateAnalysis> states = [.. previous.States];
        for (var i = 0; i < program.Files.Count; i++)
        {
            var model = program.Files[i];
            if (!dirty.Contains(model.Tree.Path))
                continue;
            var (layout, flow, state, found) = AnalyzeFile(model, previous.Cpu, project);
            layouts[i] = layout;
            flows[i] = flow;
            if (state is not null)
                states[i] = state;
            analyzed[model.Tree.Path] = found;
        }

        // A file kept from before the edit keeps its own costs, and what it costs with its
        // calls, and what it keeps, may still have moved, because a routine it calls is in the
        // file that changed.
        Flow.CallCosts.Compose(flows);
        var registers = Flow.RegisterKeeps.Compose(program.Files, layouts, flows, states);
        var reused = new ProgramAnalysis.Reuse(project, trees, conditions, analyzed, segmentTable, lengths);
        return new ProgramAnalysis(
            program, previous.Cpu, layouts, flows, states, previous.Defines, configuration,
            Collected(project, previous.Cpu, cpu, program, reused, registers))
        {
            Reused = reused,
            Reanalyzed = dirty.Count,
        };
    }

    /// <summary>
    /// What happens to one file once its names are known: its layout, where control goes and,
    /// on the 65816, the processor state; and what they found wrong.
    /// </summary>
    private static (CodeLayout Layout, Flow.ControlFlow Flow, Flow.StateAnalysis? State, IReadOnlyList<Diagnostic> Found)
        AnalyzeFile(SemanticModel model, Cpu target, ProjectSettings project)
    {
        // Where control goes is read off the order layout wrote the bytes in, so the macros
        // are expanded and the repetitions unrolled before anything is asked about the path.
        var layout = CodeLayout.Create(model, target);
        var flow = Flow.ControlFlow.Of(model, layout);
        var found = new List<Diagnostic>();

        // On the 65816 an immediate is as wide as the register it goes to, which is what the
        // processor-state analysis says. No edge depends on a length, so the analysis runs
        // over the first layout, and the file is laid out again with what it found.
        Flow.StateAnalysis? state = null;
        if (target == Cpu.Wdc65816)
        {
            state = Flow.StateAnalysis.Of(model, layout, flow, project.Ranges);
            found.AddRange(state.Diagnostics);
            layout = CodeLayout.Create(model, target, state);
            flow = Flow.ControlFlow.Of(model, layout);
        }
        found.AddRange(layout.Diagnostics);
        found.AddRange(flow.Diagnostics);

        // A family's body is written out once per instance, so a mistake in it is found once
        // for each: what every instance says is one thing to say.
        var collapsed = new List<Diagnostic>(Family.Collapsed(model.Families, found));
        var entries = flow.Regions.SelectMany(region => region.Blocks)
            .Where(block => block.IsDeclared)
            .Select(block => block.Label!);
        collapsed.AddRange(UnusedSymbols.Of(model, collapsed, entries).ToList());
        return (layout, flow, state, collapsed);
    }

    /// <summary>Everything wrong with the program, from what each part of the analysis found.</summary>
    private static IReadOnlyList<Diagnostic> Collected(
        ProjectSettings project, Cpu target, IReadOnlyList<Diagnostic> cpu, ProgramModel program,
        ProgramAnalysis.Reuse reuse, IReadOnlyList<Diagnostic> registers)
    {
        var diagnostics = new List<Diagnostic>(project.Diagnostics);
        diagnostics.AddRange(cpu);
        diagnostics.AddRange(registers);
        diagnostics.AddRange(reuse.Conditions.Values.SelectMany(found => found));
        diagnostics.AddRange(reuse.SegmentTable);
        diagnostics.AddRange(reuse.Trees.SelectMany(tree => tree.Diagnostics));
        diagnostics.AddRange(program.Diagnostics);
        diagnostics.AddRange(reuse.Analyzed.Values.SelectMany(found => found));
        if (target != Cpu.Wdc65816)
            diagnostics.AddRange(FarNeedsA65816(program.Segments, program));
        return Diagnostics.Ordered(Diagnostics.WithSeverities(diagnostics, project.Severities));
    }

    /// <summary>Diagnostics grouped by the file they are in.</summary>
    private static Dictionary<string, IReadOnlyList<Diagnostic>> ByFile(
        IEnumerable<SyntaxTree> trees, IEnumerable<Diagnostic> diagnostics)
    {
        var found = trees.ToDictionary(tree => tree.Path, _ => new List<Diagnostic>(), StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics)
        {
            if (!found.TryGetValue(diagnostic.Span.File, out var list))
                found[diagnostic.Span.File] = list = [];
            list.Add(diagnostic);
        }
        return found.ToDictionary(
            pair => pair.Key, IReadOnlyList<Diagnostic> (pair) => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The files a file's output depends on, by logical path: its own source; the sources of the
    /// modules whose interfaces it uses, and of the ones those use in turn; the files that
    /// declare segments or settings, which any file's meaning may follow from; and the files an
    /// <c>.incbin</c> in any of them names, whose lengths are what their addresses follow from.
    /// <paramref name="direct"/> keeps what each file names itself, for the next file to ask.
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
            if (SegmentTable.Declares(file.Tree) || Configuration.DeclaresSettings(file.Tree))
                found.Add(file.Tree.Path);
        }
        found.Remove(Defines.Path);
        return [.. found];

        // The other sources a file names, and the binaries it includes.
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
                if (directive.Directive.Text.Equals(".incbin", StringComparison.OrdinalIgnoreCase)
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

    /// <summary>How long the file at <paramref name="path"/> is, or null when it cannot be read.</summary>
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
    /// A far address is a bank and an offset, which only the 65816 has: ca65 refuses
    /// <c>far</c> on any earlier processor, and nt65 output that ca65 refuses is an nt65 bug
    ///. A segment or an import declared far on a 6502 or a CMOS variant is therefore reported
    /// where it is written, rather than written out for ca65 to reject.
    /// </summary>
    private static IEnumerable<Diagnostic> FarNeedsA65816(SegmentTable segments, ProgramModel program)
    {
        foreach (var segment in segments.Segments)
        {
            if (segment is { Size: AddressSize.Far, Declaration: { } declared })
                yield return new Diagnostic(declared, Catalogue.FarNeeds65816.Says($"segment \"{segment.Name}\""));
        }
        foreach (var symbol in program.Files.SelectMany(file => file.Symbols))
        {
            if (symbol is { Kind: SymbolKind.ImportedAddress, AddressSize: AddressSize.Far })
                yield return new Diagnostic(symbol.DeclarationSpan, Catalogue.FarNeeds65816.Says($"`{symbol.Name}`"));
        }
    }
}
