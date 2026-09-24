using System.Runtime.CompilerServices;
using Norristown.Syntax;

namespace Norristown.Processor;

/// <summary>
/// Determines which CPU a program is built for. A program has one CPU. It may be given on the
/// command line or by a <c>.cpu</c> item, and every file that states it must agree.
/// </summary>
public static class ProgramCpu
{
    /// <summary>The CPU a program is built for when nothing says which.</summary>
    public const Cpu Default = Cpu.Mos6502;

    // A file's `.cpu` items, read once per tree and cached. Every analysis of the program reads
    // every file, and after an edit only one of them is a tree that has not been seen before.
    private static readonly ConditionalWeakTable<SyntaxTree, List<(Cpu Cpu, TextSpan Span)>> cpuItems = new();

    /// <summary>
    /// Returns the program's CPU. <paramref name="configured"/> is the CPU the command line or
    /// the project file gives, if any. Reports an error on the line of each <c>.cpu</c> item
    /// that disagrees with it or with an earlier item.
    /// </summary>
    public static Cpu Resolve(IEnumerable<SyntaxTree> trees, Cpu? configured, List<Diagnostic> diagnostics)
    {
        var chosen = configured;
        foreach (var tree in trees.OrderBy(tree => tree.Path, StringComparer.Ordinal))
        {
            foreach (var (cpu, span) in Statements(tree))
            {
                if (chosen is { } already && cpu != already)
                {
                    diagnostics.Add(new Diagnostic(tree.GetSpan(span),
                        Catalogue.CpuDisagrees.Message(CpuNames.Format(already), CpuNames.Format(cpu))));
                    continue;
                }
                chosen = cpu;
            }
        }
        return chosen ?? Default;
    }

    /// <summary>
    /// Returns a value indicating whether any of <paramref name="trees"/> states which CPU the
    /// program is for.
    /// </summary>
    public static bool IsStated(IEnumerable<SyntaxTree> trees) => trees.Any(tree => Statements(tree).Count > 0);

    /// <summary>Returns every <c>.cpu</c> item in a file, in source order.</summary>
    private static List<(Cpu Cpu, TextSpan Span)> Statements(SyntaxTree tree) =>
        cpuItems.GetValue(tree, tree => [.. Read(tree)]);

    private static IEnumerable<(Cpu Cpu, TextSpan Span)> Read(SyntaxTree tree)
    {
        foreach (var directive in tree.Root.DescendantNodes().OfType<CpuDirectiveSyntax>())
        {
            if (CpuNames.Parse(directive.Cpu.Text) is { } cpu)
                yield return (cpu, directive.Cpu.Span);
        }
    }
}
