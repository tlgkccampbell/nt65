using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>} .else {</c>.</summary>
public sealed class ElseDirectiveSyntax : StatementSyntax
{
    internal ElseDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>}</c> that closes the branch before.</summary>
    public SyntaxToken CloseBraceToken => ChildTokens[0];

    /// <summary>The <c>.else</c>.</summary>
    public SyntaxToken Keyword => ChildTokens[1];

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);
}
