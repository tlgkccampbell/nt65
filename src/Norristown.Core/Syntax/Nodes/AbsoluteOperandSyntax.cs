using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>expr</c>, <c>expr,x</c>, <c>z:expr</c>, or the <c>expr, expr</c> of a bit branch.</summary>
public sealed class AbsoluteOperandSyntax : OperandSyntax
{
    internal AbsoluteOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c>, or null.</summary>
    public AddressPrefixSyntax? Prefix => FirstNode<AddressPrefixSyntax>();

    /// <summary>The address.</summary>
    public ExpressionSyntax Address => FirstNode<ExpressionSyntax>()!;

    /// <summary>The <c>,</c>, or null.</summary>
    public SyntaxToken? CommaToken => FirstToken(SyntaxKind.Comma);

    /// <summary>The <c>x</c>, <c>y</c> or <c>s</c> after the <c>,</c>, or null.</summary>
    public SyntaxToken? IndexRegister => FirstToken(SyntaxKind.Register);

    /// <summary>The second expression of a bit branch, or null.</summary>
    public ExpressionSyntax? Second => NodeAfter(CommaToken) as ExpressionSyntax;
}
