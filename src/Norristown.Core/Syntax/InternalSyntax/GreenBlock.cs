using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// A block: its opener line, then its contents, then the closing <c>}</c> line when it has
/// one. A block closed by a continuation line (<c>} .else {</c>) has no closer: that line
/// is the opener of the next block.
/// </summary>
public sealed class GreenBlock : GreenNode
{
    internal GreenBlock(ImmutableArray<GreenNode> children, bool hasCloser) : base(SyntaxKind.Block, SumWidths(children))
    {
        Children = children;
        HasCloser = hasCloser;
    }

    /// <summary>The opener line, the block's contents, and the closing line when it has one.</summary>
    public ImmutableArray<GreenNode> Children { get; }

    /// <summary>Whether the block ends with a <c>}</c> line of its own.</summary>
    public bool HasCloser { get; }

    /// <summary>The kind of block, from the statement its opener line ends with.</summary>
    public BlockKind BlockKind => Opener.OpensBlockKind;

    /// <summary>The line that opens the block.</summary>
    public GreenLine Opener => (GreenLine)Children[0];

    /// <summary>The closing <c>}</c> line, or null when a continuation line ends the block.</summary>
    public GreenLine? Closer => HasCloser ? (GreenLine)Children[^1] : null;

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <inheritdoc/>
    public override GreenNode GetSlot(int index) => Children[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new BlockSyntax(tree, parent, this, position);
}
