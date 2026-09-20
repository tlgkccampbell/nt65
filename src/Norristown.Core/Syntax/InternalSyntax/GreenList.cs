using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// A list-valued child of a node: its items, in source order, under one slot of the node
/// that holds them. A list with nothing in it is a slot with no node at all, so a list node
/// is normally not empty, and an empty one is still legal and reads as no items.
/// </summary>
/// <param name="children">The list's items, in source order.</param>
public sealed class GreenList(ImmutableArray<GreenNode> children)
    : GreenNode(SyntaxKind.List, SumWidths(children))
{
    /// <summary>The list's items, in source order.</summary>
    public ImmutableArray<GreenNode> Children { get; } = children;

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <inheritdoc/>
    public override GreenNode GetSlot(int index) => Children[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new SyntaxListNode(tree, parent, this, position);
}
