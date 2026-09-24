using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Represents a list whose items have a separator between them, nearly always a comma. The items
/// and the separators share one run of slots, in source order. Slots 0, 2, 4 … are the items and
/// slots 1, 3, 5 … are the separators, so a list of n items holds n − 1 separators, or n when the
/// source ends the list with one.
/// <para>
/// Items are nodes and separators are tokens, which tells the two apart.
/// </para>
/// </summary>
internal sealed class GreenSeparatedList : GreenNode
{
    /// <summary>Wraps <paramref name="children"/>, the items and separators in source order.</summary>
    /// <param name="children">The items and the separators between them, alternating.</param>
    public GreenSeparatedList(ImmutableArray<GreenNode> children) : base(SyntaxKind.SeparatedList, SumWidths(children))
    {
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is GreenToken != (i % 2 == 1))
                throw new ArgumentException("a separated list alternates an item node and a separator token", nameof(children));
        }
        Children = children;
        RollUp(children);
    }

    /// <summary>Gets the items and the separators between them, in source order.</summary>
    public ImmutableArray<GreenNode> Children { get; }

    /// <summary>Gets how many items the list has, not counting separators.</summary>
    public int Count => (Children.Length + 1) / 2;

    /// <summary>
    /// Gets how many separators the list has, which is one fewer than the items, or as many as the
    /// items when the source ends the list with one.
    /// </summary>
    public int SeparatorCount => Children.Length / 2;

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <summary>Returns the item at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
    public GreenNode Item(int index) => Children[index * 2];

    /// <summary>
    /// Returns the separator after item <paramref name="index"/>, from 0 to
    /// <see cref="SeparatorCount"/> − 1.
    /// </summary>
    public GreenToken Separator(int index) => (GreenToken)Children[(index * 2) + 1];

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => Children[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new SyntaxListNode(tree, parent, this, position);
}
