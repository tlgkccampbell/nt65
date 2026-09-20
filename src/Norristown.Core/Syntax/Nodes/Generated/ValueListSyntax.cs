// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>{ value, … }</c>: a list of elements.</summary>
public sealed class ValueListSyntax : SyntaxNode
{
    internal ValueListSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>{</c>.</summary>
    public SyntaxToken OpenBraceToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The values: expressions and braced lists or records.</summary>
    public ImmutableArray<SyntaxNode> Values => ChildNodes;

    /// <summary>The <c>}</c>, or null.</summary>
    public SyntaxToken? CloseBraceToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseBrace) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitValueList(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitValueList(this);
}
