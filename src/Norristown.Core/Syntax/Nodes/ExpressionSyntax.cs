using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>An expression.</summary>
public abstract class ExpressionSyntax : SyntaxNode
{
    private protected ExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
