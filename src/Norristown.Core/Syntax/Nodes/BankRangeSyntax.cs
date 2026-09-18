using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>$80</c> or <c>$00..$3f</c>: one bank or a range of them.</summary>
public sealed class BankRangeSyntax : SyntaxNode
{
    internal BankRangeSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The bank, or the first of the range.</summary>
    public ExpressionSyntax First => (ExpressionSyntax)ChildNodes[0];

    /// <summary>The <c>..</c>, or null.</summary>
    public SyntaxToken? DotDotToken => FirstToken(SyntaxKind.DotDot);

    /// <summary>The last bank of the range, or null.</summary>
    public ExpressionSyntax? Last => ChildNodes.Length > 1 ? (ExpressionSyntax)ChildNodes[1] : null;
}
