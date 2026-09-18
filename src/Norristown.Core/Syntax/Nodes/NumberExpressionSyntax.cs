using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A number.</summary>
public sealed class NumberExpressionSyntax : LiteralExpressionSyntax
{
    internal NumberExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
