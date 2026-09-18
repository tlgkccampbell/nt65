using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A character in single quotes.</summary>
public sealed class CharacterExpressionSyntax : LiteralExpressionSyntax
{
    internal CharacterExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
