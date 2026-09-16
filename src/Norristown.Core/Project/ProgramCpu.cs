using Norristown.Syntax;

namespace Norristown.Project;

/// <summary>
/// Which CPU a program is built for (§5.1). One per program: it may be given on the command
/// line or by a <c>.cpu</c> item, and every file that states it must agree.
/// </summary>
public static class ProgramCpu
{
    /// <summary>What a program with nothing to say about it is built for.</summary>
    public const Cpu Default = Cpu.Mos6502;

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

    /// <summary>Every <c>.cpu</c> item in a file, in source order.</summary>
    private static IEnumerable<(Cpu Cpu, TextSpan Span)> Statements(SyntaxTree tree)
    {
        foreach (var node in tree.Root.DescendantNodes())
        {
            if (node.Kind != SyntaxKind.CpuDirective)
                continue;
            foreach (var token in node.ChildTokens)
            {
                if (token.Kind is SyntaxKind.CpuName or SyntaxKind.NumberLiteral
                    && CpuNames.Parse(token.Text) is { } cpu)
                {
                    yield return (cpu, token.Span);
                    break;
                }
            }
        }
    }
}
