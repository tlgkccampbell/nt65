using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>A whole file: the lines and blocks at its top level.</summary>
/// <param name="children">The file's top-level lines and blocks, in source order.</param>
public sealed class GreenFile(ImmutableArray<GreenNode> children) : GreenNode(SyntaxKind.File, SumWidths(children))
{
    /// <summary>The file's top-level lines and blocks, in source order.</summary>
    public ImmutableArray<GreenNode> Children { get; } = children;

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => Children[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new FileSyntax(tree, parent, this, position);
}
