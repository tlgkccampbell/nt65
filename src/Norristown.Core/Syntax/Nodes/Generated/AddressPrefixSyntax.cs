// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c> before an address.</summary>
public sealed class AddressPrefixSyntax : SyntaxNode
{
    internal AddressPrefixSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The letter.</summary>
    public SyntaxToken Name => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>:</c>.</summary>
    public SyntaxToken ColonToken => Green is GreenSyntax ? ChildTokens[1] : SlotToken(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitAddressPrefix(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitAddressPrefix(this);
}
