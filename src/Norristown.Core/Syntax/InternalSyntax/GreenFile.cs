using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>Represents a whole file, which holds the lines and blocks at its top level.</summary>
internal sealed class GreenFile : GreenNode
{
    /// <summary>Wraps <paramref name="children"/>, the file's top-level lines and blocks.</summary>
    /// <param name="children">The file's top-level lines and blocks, in source order.</param>
    internal GreenFile(ImmutableArray<GreenNode> children) : base(SyntaxKind.File, SumWidths(children))
    {
        Children = children;
        RollUp(children);
    }

    /// <summary>Gets the file's top-level lines and blocks, in source order.</summary>
    public ImmutableArray<GreenNode> Children { get; }

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => Children[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new FileSyntax(tree, parent, this, position);
}
