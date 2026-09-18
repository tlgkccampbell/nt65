using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>(expr)</c> or <c>(expr),y</c>.</summary>
public sealed class IndirectOperandSyntax : OperandSyntax
{
    internal IndirectOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>(</c>.</summary>
    public SyntaxToken OpenParenToken => ChildTokens[0];

    /// <summary>The address of the pointer.</summary>
    public ExpressionSyntax Address => (ExpressionSyntax)ChildNodes[0];

    /// <summary>The <c>)</c>.</summary>
    public SyntaxToken CloseParenToken => ChildTokens[1];

    /// <summary>The <c>y</c> after the parentheses, or null.</summary>
    public SyntaxToken? IndexRegister => FirstToken(SyntaxKind.Register);
}
