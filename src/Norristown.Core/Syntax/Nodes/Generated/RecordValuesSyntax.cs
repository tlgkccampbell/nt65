// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>{ member = value, … }</c>: a record's values.</summary>
public sealed class RecordValuesSyntax : SyntaxNode
{
    private ImmutableArray<MemberValueSyntax> members;

    internal RecordValuesSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>{</c>.</summary>
    public SyntaxToken OpenBraceToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The members given a value.</summary>
    public ImmutableArray<MemberValueSyntax> Members => Nodes(ref members);

    /// <summary>The <c>}</c>, or null.</summary>
    public SyntaxToken? CloseBraceToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseBrace) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitRecordValues(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitRecordValues(this);
}
