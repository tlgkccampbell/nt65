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
    public SyntaxToken OpenBraceToken => ChildTokens[0];

    /// <summary>The values: expressions and braced lists or records.</summary>
    public ImmutableArray<SyntaxNode> Values => ChildNodes;

    /// <summary>The <c>}</c>, or null.</summary>
    public SyntaxToken? CloseBraceToken => FirstToken(SyntaxKind.CloseBrace);
}
