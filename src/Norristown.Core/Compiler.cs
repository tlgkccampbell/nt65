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
    /// <summary>Compiles <paramref name="files"/> as one program.</summary>
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files) => Compile(files, cpu: null);

    /// <summary>
    /// Compiles <paramref name="files"/> for <paramref name="cpu"/>, which is what the
    /// command line says if it says anything; a <c>.cpu</c> item must agree with it (§5.1).
    /// </summary>
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files, Cpu? cpu)
    {
        var trees = files
            .Select(SyntaxTree.Parse)
            .OrderBy(tree => tree.Path, StringComparer.Ordinal)
            .ToList();

        // The segment table and the CPU are the program's: a segment is declared exactly once
        // across it (§5.2), and it is built for one processor (§5.1).
        var diagnostics = new List<Diagnostic>();
        var segments = SegmentTable.Build(trees, diagnostics);
        var target = ProgramCpu.Resolve(trees, cpu, diagnostics);

        var outputs = new List<OutputFile>();
        foreach (var tree in trees)
        {
            diagnostics.AddRange(tree.Diagnostics);
            var model = SemanticModel.Create(tree, segments);
            diagnostics.AddRange(model.Diagnostics);

            // The 65816 needs the processor-state analysis of §7.3 before anything can be
            // written for it, which is Stage 11's; until then it is refused rather than
            // transpiled as if its widths were known.
            if (target == Cpu.Wdc65816)
                continue;

            var layout = CodeLayout.Create(model, target);
            diagnostics.AddRange(layout.Diagnostics);
            outputs.Add(Emitter.Emit(model, layout, FlatNames.Create(model, diagnostics), diagnostics));
        }

        // A program that is wrong produces no output: what would be written for it is not a
        // translation of anything.
        var ordered = Diagnostics.Ordered(diagnostics);
        if (target == Cpu.Wdc65816)
        {
            ordered = [.. ordered, .. Ca65816NotYet(trees)];
            ordered = Diagnostics.Ordered(ordered);
        }
        return new Compilation(
            ordered.Any(d => d.Severity == Severity.Error) ? [] : outputs,
            ordered);
    }

    /// <summary>Says once per program that the 65816 is a later stage's, at the line that asks for it.</summary>
    private static IEnumerable<Diagnostic> Ca65816NotYet(IEnumerable<SyntaxTree> trees)
    {
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
                        yield return new Diagnostic(tree.GetSpan(token.Span), Severity.Error,
                            "the 65816 is not transpiled yet");
                        yield break;
                    }
                }
            }
        }
    }
}
