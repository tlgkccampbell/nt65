using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>} .elseif expr {</c>.</summary>
public sealed class ElseIfDirectiveSyntax : ConditionalDirectiveSyntax
{
    internal ElseIfDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>}</c> that closes the branch before.</summary>
    public SyntaxToken CloseBraceToken => ChildTokens[0];
}
