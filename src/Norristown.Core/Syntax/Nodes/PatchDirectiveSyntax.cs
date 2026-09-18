using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.patch @op</c>: the one instruction the store above writes into.</summary>
public sealed class PatchDirectiveSyntax : StatementSyntax
{
    internal PatchDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.patch</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The label of the instruction written to, or null.</summary>
    public NameExpressionSyntax? Target => FirstNode<NameExpressionSyntax>();
}
