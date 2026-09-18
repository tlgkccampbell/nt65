using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>(a, b)</c>: the arguments of a call or of a macro call.</summary>
public sealed class ArgumentListSyntax : SyntaxNode
{
    internal ArgumentListSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>(</c>.</summary>
    public SyntaxToken OpenParenToken => ChildTokens[0];

    /// <summary>The arguments. A call's are expressions; a macro call's may also be braced operands and named arguments.</summary>
    public ImmutableArray<SyntaxNode> Arguments => ChildNodes;

    /// <summary>The <c>)</c>, or null.</summary>
    public SyntaxToken? CloseParenToken => FirstToken(SyntaxKind.CloseParen);
}
