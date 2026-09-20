using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// A parsed node: a kind and the children the parser put under it, tokens included. Every
/// token of a line ends up in exactly one node, so a statement's text is its line's text.
/// </summary>
/// <param name="kind">What the node is.</param>
/// <param name="children">The node's children, in source order.</param>
public sealed partial class GreenSyntax(SyntaxKind kind, ImmutableArray<GreenNode> children)
    : GreenNode(kind, SumWidths(children))
{
    /// <summary>The node's children, in source order.</summary>
    public ImmutableArray<GreenNode> Children { get; } = children;

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => Children[index];
}
