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
    public SyntaxToken OpenBraceToken => ChildTokens[0];

    /// <summary>The members given a value.</summary>
    public ImmutableArray<MemberValueSyntax> Members => Nodes(ref members);

    /// <summary>The <c>}</c>, or null.</summary>
    public SyntaxToken? CloseBraceToken => FirstToken(SyntaxKind.CloseBrace);
}
