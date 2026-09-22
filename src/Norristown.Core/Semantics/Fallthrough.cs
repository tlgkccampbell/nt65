using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Where a <c>.fallthrough</c> may stand. It is about the end of a routine's body rather than
/// about the statement above it, so it is the body's last line: whatever stands above it — an
/// instruction, a call, an <c>.if</c> chain with or without an <c>.else</c>, a label — every
/// path that reaches the end of the body runs into the routine it names.
/// </summary>
public static class Fallthrough
{
    /// <summary>
    /// Whether <paramref name="line"/> is the last line of a <c>.proc</c>'s body, with nothing
    /// after it but blank lines and the <c>}</c>. A macro body, a block argument and a branch of
    /// an <c>.if</c> are not a routine's body, and neither is a <c>.multiproc</c>, whose body is
    /// every member's.
    /// </summary>
    public static bool EndsABody(LineSyntax line)
    {
        if (line.Parent is not BlockSyntax { BlockKind: BlockKind.Proc, Opener.Statement: ProcDeclarationSyntax } body)
            return false;
        var members = body.Members;
        for (var i = members.IndexOf(line) + 1; i < members.Length; i++)
        {
            if (members[i] == body.Closer)
                break;
            if (members[i] is not LineSyntax { Statement: BlankLineSyntax })
                return false;
        }
        return true;
    }
}
