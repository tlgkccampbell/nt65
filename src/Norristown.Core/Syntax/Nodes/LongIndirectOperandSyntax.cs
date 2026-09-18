using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>[expr]</c> or <c>[expr],y</c>.</summary>
public sealed class LongIndirectOperandSyntax : OperandSyntax
{
    internal LongIndirectOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>[</c>.</summary>
    public SyntaxToken OpenBracketToken => ChildTokens[0];

    /// <summary>The address of the pointer.</summary>
    public ExpressionSyntax Address => (ExpressionSyntax)ChildNodes[0];

    /// <summary>The <c>]</c>, or null.</summary>
    public SyntaxToken? CloseBracketToken => FirstToken(SyntaxKind.CloseBracket);

    /// <summary>The <c>y</c> after the brackets, or null.</summary>
    public SyntaxToken? IndexRegister => FirstToken(SyntaxKind.Register);
}
