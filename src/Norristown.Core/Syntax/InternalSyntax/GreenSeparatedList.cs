using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// A list whose items are written with a separator between them, nearly always a comma. The
/// items and the separators share one run of slots, in source order: slots 0, 2, 4 … are the
/// items and slots 1, 3, 5 … the separators, so a list of n items holds n − 1 separators, or
/// n when the source ends the list with one.
/// <para>
/// Items are nodes and separators are tokens, which is what tells the two apart.
/// </para>
/// </summary>
public sealed class GreenSeparatedList : GreenNode
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
        ContainsDiagnostics = AnyDiagnostics(children);
    }

    /// <summary>The items and the separators between them, in source order.</summary>
    public ImmutableArray<GreenNode> Children { get; }

    /// <summary>How many items the list has, separators aside.</summary>
    public int Count => (Children.Length + 1) / 2;

    /// <summary>How many separators the list has: one fewer than the items, or as many.</summary>
    public int SeparatorCount => Children.Length / 2;

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <summary>The item at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
    public GreenNode Item(int index) => Children[index * 2];

    /// <summary>The separator after item <paramref name="index"/>, from 0 to <see cref="SeparatorCount"/> − 1.</summary>
    public GreenToken Separator(int index) => (GreenToken)Children[(index * 2) + 1];

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => Children[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new SyntaxListNode(tree, parent, this, position);
}
