using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A string in double quotes.</summary>
public sealed class StringExpressionSyntax : LiteralExpressionSyntax
{
    internal StringExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
