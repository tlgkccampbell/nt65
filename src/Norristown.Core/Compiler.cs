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
    /// command line says if it says anything; a <c>.cpu</c> item must agree with it (§5.1).
    /// </summary>
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files, Cpu? cpu) =>
        Compile(files, ProjectSettings.None with { Cpu = cpu });

    /// <summary>Compiles <paramref name="files"/> as the program <paramref name="project"/> describes (§5.3).</summary>
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files, ProjectSettings project)
    {
        var analysis = Analyze(files, project);
        var diagnostics = new List<Diagnostic>(analysis.Diagnostics);
        var outputs = new List<OutputFile>();

        // Every file is written even when the program is already wrong, because what emission
        // finds — a construct no stage has reached, two names that meet in the output — is
        // worth reporting alongside the rest rather than only once the rest is fixed.
        for (var i = 0; i < analysis.Layouts.Count; i++)
        {
            var model = analysis.Program.Files[i];

            // The defines are not a file anyone wrote, and nothing is written for them (§5.3).
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
    public static ProgramAnalysis Analyze(IReadOnlyCollection<SyntaxTree> files, ProjectSettings project)
    {
        var trees = files.OrderBy(tree => tree.Path, StringComparer.Ordinal).ToList();

        // The build configuration is read as a file of constants, so that defines are ordinary
        // symbols to scoping and evaluation; only emission treats them differently (§5.3).
        var defines = Defines.Source(project.Defines) is { } source ? SyntaxTree.Parse(source) : null;
        if (defines is not null)
            trees.Add(defines);

        // The segment table and the CPU are the program's: a segment is declared exactly once
        // across it (§5.2), and it is built for one processor (§5.1).
        var diagnostics = new List<Diagnostic>(project.Diagnostics);
        var segments = SegmentTable.Build(trees, project.Segments, diagnostics);
        var target = ProgramCpu.Resolve(trees, project.Cpu, diagnostics);

        // Every file is read before any is resolved, because a name one file uses may be one
        // another file exports (§12).
        var program = ProgramModel.Create(trees, segments, defines);
        diagnostics.AddRange(trees.SelectMany(tree => tree.Diagnostics));
        diagnostics.AddRange(program.Diagnostics);

        // The 65816 needs the processor-state analysis of §7.3 before its instructions can be
        // sized, which is Stage 11's; until then it is refused rather than sized as if its
        // widths were known.
        var layouts = new List<CodeLayout>();
        if (target == Cpu.Wdc65816)
        {
            diagnostics.AddRange(Ca65816NotYet(trees));
        }
        else
        {
            diagnostics.AddRange(FarNeedsA65816(segments, program));
            foreach (var model in program.Files)
            {
                var layout = CodeLayout.Create(model, target);
                layouts.Add(layout);
                diagnostics.AddRange(layout.Diagnostics);
            }
        }
        return new ProgramAnalysis(program, target, layouts, defines, Diagnostics.Ordered(diagnostics));
    }

    /// <summary>
    /// A far address is a bank and an offset, which only the 65816 has: ca65 refuses
    /// <c>far</c> on any earlier processor, and nt65 output that ca65 refuses is an nt65 bug
    /// (§3.2). A segment or an import declared far on a 6502 or 65C02 is therefore reported
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

    /// <summary>
    /// Says once per program that the 65816 is a later stage's, at the line that asks for it,
    /// or at the top of the first file when the command line or the project asked instead.
    /// </summary>
    private static IEnumerable<Diagnostic> Ca65816NotYet(IReadOnlyList<SyntaxTree> trees)
    {
        const string Message = "the 65816 is not transpiled yet";
        foreach (var tree in trees)
        {
            foreach (var node in tree.Root.DescendantNodes())
            {
                if (node.Kind != SyntaxKind.CpuDirective)
                    continue;
                foreach (var token in node.ChildTokens)
                {
                    if (token.Kind is SyntaxKind.CpuName or SyntaxKind.NumberLiteral
                        && CpuNames.Parse(token.Text) == Cpu.Wdc65816)
                    {
                        yield return new Diagnostic(tree.GetSpan(token.Span), Severity.Error, Message);
                        yield break;
                    }
                }
            }
        }

        if (trees.Count > 0)
            yield return new Diagnostic(new Span(trees[0].Path, 1, 1, 1), Severity.Error, Message);
    }
}
