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
    /// <c>.incbin</c> names is, for a caller whose files are not where the paths say.
    /// </summary>
    public static Compilation Compile(
        IReadOnlyCollection<SourceFile> files, ProjectSettings project, Func<string, long?> binaryLength) =>
        Emit(Analyze([.. files.Select(SyntaxTree.Parse)], project, binaryLength), project);

    /// <summary>Writes out the program <paramref name="analysis"/> worked out.</summary>
    public static Compilation Emit(ProgramAnalysis analysis, ProjectSettings project)
    {
        var diagnostics = new List<Diagnostic>(analysis.Diagnostics);
        var outputs = new List<OutputFile>();

        // Every file is written even when the program is already wrong, because what emission
        // finds — a construct no stage has reached, two names that meet in the output — is
        // worth reporting alongside the rest rather than only once the rest is fixed.
        for (var i = 0; i < analysis.Layouts.Count; i++)
        {
            var model = analysis.Program.Files[i];

            // The defines are not a file anyone wrote, and nothing is written for them.
            if (model.Tree == analysis.Defines)
                continue;
            outputs.Add(Emitter.Emit(
                model, analysis.Layouts[i], FlatNames.Create(model, diagnostics), diagnostics, project.Out));
        }

        // A program that is wrong produces no output: what would be written for it is not a
        // translation of anything.
        var ordered = Diagnostics.Ordered(diagnostics);
        return new Compilation(ordered.Any(d => d.Severity == Severity.Error) ? [] : outputs, ordered);
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
        ProgramAnalysis? previous) =>
        previous is not null && Reanalyze(previous, files, project, binaryLength) is { } reused
            ? reused
            : AnalyzeAll(files, project, binaryLength);

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
        var defines = Defines.Source(project.Defines) is { } source ? SyntaxTree.Parse(source) : null;
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
        var program = ProgramModel.Create(trees, segments, configuration, defines, Length);

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

        var reuse = new ProgramAnalysis.Reuse(
            project, trees, ByFile(trees, conditions), analyzed, segmentTable, lengths);
        return new ProgramAnalysis(
            program, target, layouts, flows, states, defines, configuration,
            Collected(project, target, cpu, program, reuse))
        {
            Reused = reuse,
            Reanalyzed = program.Files.Count,
        };
    }

    /// <summary>
    /// The program <paramref name="previous"/> analyzed, with one file changed, analyzing only
    /// that file; or null when the change may reach further than that file and the whole
    /// program has to be analyzed again. Anything decided for the program as a whole changing
    /// — the files in it, the project, the CPU, the segments — is such a change, and so is
    /// the file's interface changing.
    /// </summary>
    private static ProgramAnalysis? Reanalyze(
        ProgramAnalysis previous, IReadOnlyCollection<SyntaxTree> files, ProjectSettings project,
        Func<string, long?> binaryLength)
    {
        if (previous.Reused is not { } reuse || reuse.Project != project)
            return null;
        var sources = files.ToDictionary(tree => tree.Path, StringComparer.Ordinal);
        var written = reuse.Trees.Where(tree => tree != previous.Defines).ToList();
        if (sources.Count != written.Count || written.Any(tree => !sources.ContainsKey(tree.Path)))
            return null;
        var changed = written.Where(tree => sources[tree.Path] != tree).ToList();
        if (changed.Count == 0)
            return previous;
        if (changed.Count > 1)
            return null;

        // An `.incbin` file that changed on disk changes every file that includes it.
        if (reuse.Lengths.Any(pair => binaryLength(pair.Key) != pair.Value))
            return null;
        var lengths = new ConcurrentDictionary<string, long?>(reuse.Lengths, StringComparer.Ordinal);
        long? Length(string path) => lengths.GetOrAdd(path, binaryLength);

        var before = changed[0];
        var after = sources[before.Path];
        if (SegmentTable.Declares(before) || SegmentTable.Declares(after))
            return null;
        List<SyntaxTree> trees = [.. reuse.Trees.Select(tree => tree == before ? after : tree)];
        var cpu = new List<Diagnostic>();
        if (ProgramCpu.Resolve(trees, project.Cpu, cpu) != previous.Cpu)
            return null;

        var conditions = new List<Diagnostic>();
        var configuration = previous.Configuration.Replacing(before, after, previous.Cpu, project.Defines, conditions);
        var edit = new EditMap(before, after);
        if (previous.Program.Replacing(before, after, configuration, previous.Defines, edit.Moved, Length)
            is not { } program)
        {
            return null;
        }

        // What every other file's analysis found stands, carried to where the edit moved it.
        if (edit.Moved(reuse.Conditions, before.Path) is not { } conditionsNow
            || edit.Moved(reuse.Analyzed, before.Path) is not { } analyzedNow
            || edit.Moved(reuse.SegmentTable) is not { } segmentTable)
        {
            return null;
        }

        var index = program.Files.ToList().FindIndex(file => file.Tree == after);
        var (layout, flow, state, found) = AnalyzeFile(program.Files[index], previous.Cpu, project);
        List<CodeLayout> layouts = [.. previous.Layouts];
        List<Flow.ControlFlow> flows = [.. previous.Flows];
        List<Flow.StateAnalysis> states = [.. previous.States];
        layouts[index] = layout;
        flows[index] = flow;
        if (state is not null)
            states[index] = state;
        conditionsNow[after.Path] = conditions;
        analyzedNow[after.Path] = found;

        var reused = new ProgramAnalysis.Reuse(project, trees, conditionsNow, analyzedNow, segmentTable, lengths);
        return new ProgramAnalysis(
            program, previous.Cpu, layouts, flows, states, previous.Defines, configuration,
            Collected(project, previous.Cpu, cpu, program, reused))
        {
            Reused = reused,
            Reanalyzed = 1,
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
        return (layout, flow, state, found);
    }

    /// <summary>Everything wrong with the program, from what each part of the analysis found.</summary>
    private static IReadOnlyList<Diagnostic> Collected(
        ProjectSettings project, Cpu target, IReadOnlyList<Diagnostic> cpu, ProgramModel program,
        ProgramAnalysis.Reuse reuse)
    {
        var diagnostics = new List<Diagnostic>(project.Diagnostics);
        diagnostics.AddRange(cpu);
        diagnostics.AddRange(reuse.Conditions.Values.SelectMany(found => found));
        diagnostics.AddRange(reuse.SegmentTable);
        diagnostics.AddRange(reuse.Trees.SelectMany(tree => tree.Diagnostics));
        diagnostics.AddRange(program.Diagnostics);
        diagnostics.AddRange(reuse.Analyzed.Values.SelectMany(found => found));
        if (target != Cpu.Wdc65816)
            diagnostics.AddRange(FarNeedsA65816(program.Segments, program));
        return Diagnostics.Ordered(diagnostics);
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
    ///. A segment or an import declared far on a 6502 or 65C02 is therefore reported
    /// where it is written, rather than written out for ca65 to reject.
    /// </summary>
    private static IEnumerable<Diagnostic> FarNeedsA65816(SegmentTable segments, ProgramModel program)
    {
        const string Message = "a `far` address needs the 65816";
        foreach (var segment in segments.Segments)
        {
            if (segment is { Size: AddressSize.Far, Declaration: { } declared })
                yield return new Diagnostic(declared, Severity.Error, $"segment \"{segment.Name}\": {Message}");
        }
        foreach (var symbol in program.Files.SelectMany(file => file.Symbols))
        {
            if (symbol is { Kind: SymbolKind.ImportedAddress, AddressSize: AddressSize.Far })
                yield return new Diagnostic(symbol.DeclarationSpan, Severity.Error, $"`{symbol.Name}`: {Message}");
        }
    }
}
