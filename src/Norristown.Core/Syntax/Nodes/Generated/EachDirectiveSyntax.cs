// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.each what, name {</c>.</summary>
public sealed class EachDirectiveSyntax : RepetitionDirectiveSyntax
{
    internal EachDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <inheritdoc/>
    public override SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <inheritdoc/>
    public override ExpressionSyntax Expression =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>()! : SlotNode<ExpressionSyntax>(1);

    /// <inheritdoc/>
    public override SyntaxToken? CommaToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Comma) : SlotTokenOrNull(2);

    /// <inheritdoc/>
    public override SyntaxToken? Name =>
        Green is GreenSyntax ? TokenAfter(CommaToken) is { Kind: SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic } name ? name : null : SlotTokenOrNull(3);

    /// <inheritdoc/>
    public override SyntaxToken? OpenBraceToken =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotToken(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitEachDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitEachDirective(this);
}
