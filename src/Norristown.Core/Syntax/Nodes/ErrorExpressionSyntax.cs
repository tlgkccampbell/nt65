using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>Where an expression was expected and none could be read.</summary>
public sealed class ErrorExpressionSyntax : ExpressionSyntax
{
    internal ErrorExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The directive that was read as far as it went, or null.</summary>
    public SyntaxToken? Token => TokenAt(0);
}
