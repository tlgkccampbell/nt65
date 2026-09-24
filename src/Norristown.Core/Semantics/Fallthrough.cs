using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Decides where a <c>.fallthrough</c> is allowed. The directive applies to the end of a routine's
/// body, not to the statement above it, so it must be the body's last line once the
/// configuration's conditionals are resolved. Whatever is above it, such as an instruction, a call
/// or a label, the directive states that every path reaching the end of the body continues into
/// the routine it names.
/// <para>
/// A branch of an <c>.if</c> chain that is itself the last thing in the body ends the body in the
/// configurations that take that branch. A <c>.fallthrough</c> may therefore also be the last line
/// of such a branch, at any depth. Conditions are resolved before analysis, so in each
/// configuration at most one such <c>.fallthrough</c> remains, and it is last.
/// </para>
/// </summary>
public static class Fallthrough
{
    /// <summary>
    /// Returns a value indicating whether <paramref name="line"/> is the last line of a
    /// <c>.proc</c>'s body, with nothing after it but blank lines and the <c>}</c>. It also
    /// returns true for the last line of a branch of an <c>.if</c> chain that ends such a body with
    /// nothing after the chain. A macro body, a block argument and a repetition are not a
    /// routine's body. Neither is a <c>.multiproc</c>'s body, because it is the body of every
    /// member.
    /// </summary>
    public static bool EndsABody(LineSyntax line) =>
        line.Parent is BlockSyntax block && NothingAfter(block, line, continuations: false) && Ends(block);

    /// <summary>
    /// Returns a value indicating whether reaching the end of <paramref name="block"/> means
    /// reaching the end of a routine's body.
    /// </summary>
    private static bool Ends(BlockSyntax block) => block switch
    {
        { BlockKind: BlockKind.Proc, Opener.Statement: ProcDeclarationSyntax } => true,
        { BlockKind: BlockKind.If, Parent: BlockSyntax around } =>
            NothingAfter(around, block, continuations: true) && Ends(around),
        _ => false,
    };

    /// <summary>
    /// Returns a value indicating whether <paramref name="member"/> is the last thing
    /// <paramref name="block"/> contains, with only blank lines after it before the <c>}</c>.
    /// When <paramref name="continuations"/> is true, the <c>.elseif</c> and <c>.else</c> branches
    /// that continue the member's chain may also follow it.
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
