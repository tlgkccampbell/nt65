using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>*</c>: the address of the statement it is written in.</summary>
public sealed class CurrentAddressExpressionSyntax : ExpressionSyntax
{
    internal CurrentAddressExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>*</c>.</summary>
    public SyntaxToken StarToken => ChildTokens[0];
}
