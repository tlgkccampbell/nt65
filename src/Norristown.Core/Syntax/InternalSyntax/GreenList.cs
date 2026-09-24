using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Represents a list-valued child of a node, which holds its items in source order under one
/// slot of the node. A list with nothing in it is a slot with no node at all, so a list node is
/// normally not empty, but an empty one is still legal and reads as no items.
/// </summary>
internal sealed class GreenList : GreenNode
{
    /// <summary>Wraps <paramref name="children"/>, the list's items in source order.</summary>
    /// <param name="children">The list's items, in source order.</param>
    public GreenList(ImmutableArray<GreenNode> children) : base(SyntaxKind.List, SumWidths(children))
    {
        Children = children;
        RollUp(children);
    }

    /// <summary>Gets the list's items, in source order.</summary>
    public ImmutableArray<GreenNode> Children { get; }

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => Children[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new SyntaxListNode(tree, parent, this, position);
}
