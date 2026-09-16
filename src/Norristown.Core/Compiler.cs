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
        IReadOnlyCollection<SourceFile> files, ProjectSettings project, Func<string, long?> binaryLength)
    {
        var analysis = Analyze([.. files.Select(SyntaxTree.Parse)], project, binaryLength);
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
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, Func<string, long?> binaryLength)
    {
        var trees = files.OrderBy(tree => tree.Path, StringComparer.Ordinal).ToList();

        // The build configuration is read as a file of constants, so that defines are ordinary
        // symbols to scoping and evaluation; only emission treats them differently.
        var defines = Defines.Source(project.Defines) is { } source ? SyntaxTree.Parse(source) : null;
        if (defines is not null)
            trees.Add(defines);

        // The segment table and the CPU are the program's: a segment is declared exactly once
        // across it, and it is built for one processor.
        var diagnostics = new List<Diagnostic>(project.Diagnostics);
        var target = ProgramCpu.Resolve(trees, project.Cpu, diagnostics);

        // Which `.if` branches the build takes is settled first: conditions test the
        // configuration and nothing else, and which segments and declarations a program has
        // follows from the answers.
        var configuration = Configuration.Resolve(trees, target, project.Defines, diagnostics);
        var segments = SegmentTable.Build(trees, project.Segments, configuration, diagnostics);

        // Every file is read before any is resolved, because a name one file uses may be one
        // another file exports.
        var program = ProgramModel.Create(trees, segments, configuration, defines, binaryLength);
        diagnostics.AddRange(trees.SelectMany(tree => tree.Diagnostics));
        diagnostics.AddRange(program.Diagnostics);

        var layouts = new List<CodeLayout>();
        var flows = new List<Flow.ControlFlow>();
        var states = new List<Flow.StateAnalysis>();
        if (target != Cpu.Wdc65816)
            diagnostics.AddRange(FarNeedsA65816(segments, program));
        foreach (var model in program.Files)
        {
            // Where control goes is read off the order layout wrote the bytes in, so the macros
            // are expanded and the repetitions unrolled before anything is asked about the path.
            var layout = CodeLayout.Create(model, target);
            var flow = Flow.ControlFlow.Of(model, layout);

            // On the 65816 an immediate is as wide as the register it goes to, which is what the
            // processor-state analysis says. No edge depends on a length, so the analysis runs
            // over the first layout, and the file is laid out again with what it found.
            if (target == Cpu.Wdc65816)
            {
                var state = Flow.StateAnalysis.Of(model, layout, flow, project.Ranges);
                states.Add(state);
                diagnostics.AddRange(state.Diagnostics);
                layout = CodeLayout.Create(model, target, state);
                flow = Flow.ControlFlow.Of(model, layout);
            }
            layouts.Add(layout);
            flows.Add(flow);
            diagnostics.AddRange(layout.Diagnostics);
            diagnostics.AddRange(flow.Diagnostics);
        }
        return new ProgramAnalysis(
            program, target, layouts, flows, states, defines, configuration, Diagnostics.Ordered(diagnostics));
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
