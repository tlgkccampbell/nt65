// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A name: <c>label</c>, <c>@local</c>, <c>gfx::init</c>, <c>::top_level</c>, <c>table[2]::x</c>.</summary>
public sealed partial class NameExpressionSyntax : ExpressionSyntax
{
    private ImmutableArray<SyntaxToken> names;
    private ImmutableArray<ElementIndexSyntax> indexes;

    internal NameExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The leading <c>::</c> of a name written from the top level, or null.</summary>
    public SyntaxToken? GlobalToken => TokenAt(0) is { Kind: SyntaxKind.ColonColon } global ? global : null;

    /// <summary>The names between the <c>::</c>, outermost first. Empty when not even the first name was written.</summary>
    public ImmutableArray<SyntaxToken> Names
    {
        get
        {
            if (names.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref names, [.. ChildTokens.Where(token => token.Kind != SyntaxKind.ColonColon)]);
            return names;
        }
    }

    /// <summary>Every <c>[i]</c> written in the name.</summary>
    public ImmutableArray<ElementIndexSyntax> Indexes => Nodes(ref indexes);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitNameExpression(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitNameExpression(this);
}
