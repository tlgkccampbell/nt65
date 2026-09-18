using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// A block: its opener line, then its contents, then the closing <c>}</c> line when it has
/// one. A block closed by a continuation line (<c>} .else {</c>) has no closer: that line
/// is the opener of the next block.
/// </summary>
public sealed class BlockSyntax : SyntaxNode
{
    internal BlockSyntax(SyntaxTree tree, SyntaxNode? parent, GreenBlock green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The green block this one wraps.</summary>
    public new GreenBlock Green => (GreenBlock)base.Green;

    /// <summary>The kind of block, from the statement its opener line ends with.</summary>
    public BlockKind BlockKind => Green.BlockKind;

    /// <summary>Whether the block ends with a <c>}</c> line of its own.</summary>
    public bool HasCloser => Green.HasCloser;

    /// <summary>The line that opens the block.</summary>
    public LineSyntax Opener => (LineSyntax)ChildNodes[0];

    /// <summary>The closing <c>}</c> line, or null when a continuation line ends the block.</summary>
    public LineSyntax? Closer => HasCloser ? (LineSyntax)ChildNodes[^1] : null;

    /// <summary>The opener line, the block's contents, and the closing line when it has one.</summary>
    public ImmutableArray<SyntaxNode> Members => ChildNodes;
}
