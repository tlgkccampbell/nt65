using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Where a <c>.fallthrough</c> is allowed. It applies to the end of a routine's body rather
/// than to the statement above it, so it must be the body's last line once the configuration's
/// conditionals are resolved: whatever is above it — an instruction, a call, a label — it says
/// that every path reaching the end of the body runs on into the routine it names. A branch of
/// an <c>.if</c> chain that is itself the last thing in the body ends the body in the
/// configurations that take it, so a <c>.fallthrough</c> may also be the last line of such a
/// branch, to any depth; conditions are settled before analysis, so in each configuration at
/// most one of them remains, and it is last.
/// </summary>
public static class Fallthrough
{
    /// <summary>
    /// Whether <paramref name="line"/> is the last line of a <c>.proc</c>'s body, with nothing
    /// after it but blank lines and the <c>}</c>, or the last line of a branch of an <c>.if</c>
    /// chain that ends such a body with nothing after the chain. A macro body, a block argument
    /// and a repetition are not a routine's body, and neither is a <c>.multiproc</c>, whose body
    /// is every member's.
    /// </summary>
    public static bool EndsABody(LineSyntax line) =>
        line.Parent is BlockSyntax block && NothingAfter(block, line, continuations: false) && Ends(block);

    /// <summary>Whether reaching the end of <paramref name="block"/> is reaching the end of a routine's body.</summary>
    private static bool Ends(BlockSyntax block) => block switch
    {
        { BlockKind: BlockKind.Proc, Opener.Statement: ProcDeclarationSyntax } => true,
        { BlockKind: BlockKind.If, Parent: BlockSyntax around } =>
            NothingAfter(around, block, continuations: true) && Ends(around),
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="member"/> is the last thing <paramref name="block"/> holds, with
    /// only blank lines after it before the <c>}</c>, and with <paramref name="continuations"/>
    /// the <c>.elseif</c> and <c>.else</c> branches that continue its chain too.
    /// </summary>
    private static bool NothingAfter(BlockSyntax block, SyntaxNode member, bool continuations)
    {
        var members = block.Members;
        for (var i = members.IndexOf(member) + 1; i < members.Length; i++)
        {
            if (members[i] == block.Closer)
                break;
            if (members[i] is LineSyntax { Statement: BlankLineSyntax })
                continue;
            if (continuations && members[i] is BlockSyntax
                {
                    BlockKind: BlockKind.If, Opener.Statement: ElseIfDirectiveSyntax or ElseDirectiveSyntax,
                })
            {
                continue;
            }
            return false;
        }
        return true;
    }
}
