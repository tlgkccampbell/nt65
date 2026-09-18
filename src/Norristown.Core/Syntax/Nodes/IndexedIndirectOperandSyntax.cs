using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>(expr,x)</c> or <c>(expr,s),y</c>.</summary>
public sealed class IndexedIndirectOperandSyntax : OperandSyntax
{
    internal IndexedIndirectOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>(</c>.</summary>
    public SyntaxToken OpenParenToken => ChildTokens[0];

    /// <summary>The address of the pointer.</summary>
    public ExpressionSyntax Address => (ExpressionSyntax)ChildNodes[0];

    /// <summary>The <c>x</c> or <c>s</c> inside the parentheses.</summary>
    public SyntaxToken InnerRegister => ChildTokens[2];

    /// <summary>The <c>)</c>.</summary>
    public SyntaxToken CloseParenToken => ChildTokens[3];

    /// <summary>The <c>y</c> after the parentheses, or null.</summary>
    public SyntaxToken? OuterRegister => TokenAt(5);
}
