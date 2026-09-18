using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name = expr</c>.</summary>
public sealed class ConstantDeclarationSyntax : StatementSyntax
{
    internal ConstantDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The constant's name.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>=</c>.</summary>
    public SyntaxToken EqualsToken => ChildTokens[1];

    /// <summary>The constant's value.</summary>
    public ExpressionSyntax Value => FirstNode<ExpressionSyntax>()!;
}
