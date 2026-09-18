using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A line that opens a branch with a condition: <c>.if expr {</c> or <c>} .elseif expr {</c>.</summary>
public abstract class ConditionalDirectiveSyntax : StatementSyntax
{
    private protected ConditionalDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.if</c> or <c>.elseif</c>.</summary>
    public SyntaxToken Keyword => FirstToken(SyntaxKind.Directive)!.Value;

    /// <summary>The condition the branch is taken on.</summary>
    public ExpressionSyntax Condition => FirstNode<ExpressionSyntax>()!;

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);
}
