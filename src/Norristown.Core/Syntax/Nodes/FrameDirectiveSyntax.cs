using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.frame locals: Locals</c>: a name, and the struct the top of the stack is laid out as.</summary>
public sealed class FrameDirectiveSyntax : StatementSyntax
{
    internal FrameDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.frame</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The frame's name, or null.</summary>
    public SyntaxToken? Name => TokenAt(1) is { Kind: SyntaxKind.Identifier } name ? name : null;

    /// <summary>The <c>:</c>, or null.</summary>
    public SyntaxToken? ColonToken => FirstToken(SyntaxKind.Colon);

    /// <summary>The struct the frame is laid out as, or null.</summary>
    public ExpressionSyntax? Type => FirstNode<ExpressionSyntax>();
}
