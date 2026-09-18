using System.Runtime.CompilerServices;
using Norristown.Syntax;

namespace Norristown.Project;

/// <summary>
/// Which CPU a program is built for. One per program: it may be given on the command
/// line or by a <c>.cpu</c> item, and every file that states it must agree.
/// </summary>
public static class ProgramCpu
{
    /// <summary>What a program with nothing to say about it is built for.</summary>
    public const Cpu Default = Cpu.Mos6502;

    // A file's `.cpu` items, read once per tree: every analysis of the program asks every file,
    // and after an edit only one of them is a tree it has not seen.
    private static readonly ConditionalWeakTable<SyntaxTree, List<(Cpu Cpu, TextSpan Span)>> written = new();

    /// <summary>
    /// The program's CPU. <paramref name="configured"/> is what the command line or the
    /// project file says, if anything; a <c>.cpu</c> item that disagrees with it, or with an
    /// earlier one, is an error on its own line.
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
                    diagnostics.Add(new Diagnostic(tree.GetSpan(span), Severity.Error,
                        $"this program is built for the {CpuNames.Spell(already)}, so `.cpu {CpuNames.Spell(cpu)}` disagrees"));
                    continue;
                }
                chosen = cpu;
            }
        }
        return chosen ?? Default;
    }

    /// <summary>Whether any of <paramref name="trees"/> says which CPU the program is for.</summary>
    public static bool IsStated(IEnumerable<SyntaxTree> trees) => trees.Any(tree => Statements(tree).Count > 0);

    /// <summary>Every <c>.cpu</c> item in a file, in source order.</summary>
    private static List<(Cpu Cpu, TextSpan Span)> Statements(SyntaxTree tree) =>
        written.GetValue(tree, tree => [.. Read(tree)]);

    private static IEnumerable<(Cpu Cpu, TextSpan Span)> Read(SyntaxTree tree)
    {
        foreach (var directive in tree.Root.DescendantNodes().OfType<CpuDirectiveSyntax>())
        {
            if (directive.Cpu is { } name && CpuNames.Parse(name.Text) is { } cpu)
                yield return (cpu, name.Span);
        }
    }
}
