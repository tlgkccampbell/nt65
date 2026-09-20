// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A state word and the <c>*</c> or <c>?</c> after it: <c>a16</c>, <c>i*</c>, <c>e?</c>.</summary>
public sealed class StateFlagItemSyntax : StateItemSyntax
{
    internal StateFlagItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The item's word.</summary>
    public new SyntaxToken Name => SlotToken(0);

    /// <summary>The <c>*</c> or <c>?</c> after the word, or null.</summary>
    public SyntaxToken? SuffixToken => SlotTokenOrNull(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitStateFlagItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitStateFlagItem(this);
}
