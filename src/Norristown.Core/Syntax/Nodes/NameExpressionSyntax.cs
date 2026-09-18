using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A name: <c>label</c>, <c>@local</c>, <c>gfx::init</c>, <c>::top_level</c>, <c>table[2]::x</c>.</summary>
public sealed class NameExpressionSyntax : ExpressionSyntax
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

    /// <summary>The <c>[i]</c> written straight after <paramref name="name"/>, one of <see cref="Names"/>, or null.</summary>
    public ElementIndexSyntax? IndexAfter(SyntaxToken name) => NodeAfter(name) as ElementIndexSyntax;
}
