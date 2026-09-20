// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>} name {</c>: the line that closes one block argument of a macro call and opens the next.</summary>
public sealed class BlockContinuationSyntax : StatementSyntax
{
    internal BlockContinuationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>}</c>.</summary>
    public SyntaxToken CloseBraceToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The parameter the next block is given to.</summary>
    public SyntaxToken Name => Green is GreenSyntax ? ChildTokens[1] : SlotToken(1);

    /// <summary>The <c>{</c>.</summary>
    public SyntaxToken OpenBraceToken => Green is GreenSyntax ? ChildTokens[2] : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitBlockContinuation(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitBlockContinuation(this);
}
