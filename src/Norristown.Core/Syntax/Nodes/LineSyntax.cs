using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// One source line. Its tokens are reachable both directly and through its
/// <see cref="Statement"/>, which holds the same tokens in the same order.
/// </summary>
public sealed class LineSyntax : SyntaxNode
{
    private StatementSyntax? statement;

    internal LineSyntax(SyntaxTree tree, SyntaxNode? parent, GreenLine green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The green line this one wraps.</summary>
    public new GreenLine Green => (GreenLine)base.Green;

    /// <summary>What kind of line this is, from its tokens alone.</summary>
    public LineKind LineKind => Green.LineKind;

    /// <summary>
    /// For a line that opens a block, the kind of block; <see cref="BlockKind.Region"/> for a
    /// <c>.segment NAME</c> region line; otherwise <see cref="BlockKind.None"/>.
    /// </summary>
    public BlockKind OpensBlockKind => Green.OpensBlockKind;

    /// <summary>
    /// What the line's tokens parse to.
    /// <para>
    /// A declaration written after <c>.export</c> is the line's statement, so it reads as the
    /// same declaration without it; <see cref="StatementSyntax.IsExported"/> says the
    /// <c>.export</c> is there.
    /// </para>
    /// </summary>
    public StatementSyntax Statement => statement ??= Parsed();

    private protected override ImmutableArray<SyntaxNode> CreateChildNodes() => [Statement];

    private StatementSyntax Parsed()
    {
        var parsed = (StatementSyntax)Tree.Statement(LineIndex).CreateRed(Tree, this, Position);
        return parsed is ExportedDeclarationSyntax exported ? exported.Declaration : parsed;
    }
}
