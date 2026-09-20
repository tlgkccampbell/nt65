// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
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
    public SyntaxToken CloseBraceToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>.else</c>.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[1] : SlotToken(1);

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitElseDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitElseDirective(this);
}
