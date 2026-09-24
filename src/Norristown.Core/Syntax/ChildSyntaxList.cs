using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents a node's child nodes and tokens together, in source order. The list is a view over
/// the node, so getting it costs nothing. A child node's red node is created the first time it is
/// requested and then cached by the parent.
/// <para>
/// An empty slot contributes no child. A slot that holds a list contributes the list's items and
/// separators rather than the node over them, as in Roslyn, so an analyzer walking a node's
/// children never sees a list node.
/// </para>
/// <para>
/// A line is the exception to reading children from slots. It stores its tokens directly in its
/// slots, but its children are the pieces the line is made of, which contain those same tokens. A
/// walk of the children therefore reaches every token of a file once, under the node it is part of.
/// </para>
/// </summary>
public readonly struct ChildSyntaxList : IEnumerable<SyntaxNodeOrToken>
{
    private readonly SyntaxNode? node;

    internal ChildSyntaxList(SyntaxNode node) => this.node = node;

    /// <summary>Gets the number of children the node has.</summary>
    public int Count
    {
        get
        {
            if (node is null)
                return 0;
            if (node.RedChildren is { } children)
                return children.Length;
            var count = 0;
            for (var i = 0; i < node.Green.SlotCount; i++)
            {
                if (node.Green.GetSlot(i) is { } slot)
                    count += SyntaxNode.IsList(slot) ? new ChildSyntaxList(node.SlotRed(i)!).Count : 1;
            }
            return count;
        }
    }

    /// <summary>Gets the child at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
    public SyntaxNodeOrToken this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            foreach (var child in this)
            {
                if (index-- == 0)
                    return child;
            }
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    /// <summary>
    /// Returns the <paramref name="length"/> children starting at <paramref name="start"/>. C#
    /// slice patterns call this method.
    /// </summary>
    /// <param name="start">The index of the first child to take.</param>
    /// <param name="length">The number of children to take.</param>
    public ImmutableArray<SyntaxNodeOrToken> Slice(int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxNodeOrToken>(length);
        for (var i = 0; i < length; i++)
            builder.Add(this[start + i]);
        return builder.MoveToImmutable();
    }

    /// <summary>Returns an enumerator over the children in source order.</summary>
    public IEnumerator<SyntaxNodeOrToken> GetEnumerator()
    {
        if (node is null)
            yield break;
        if (node.RedChildren is { } shown)
        {
            foreach (var child in shown)
                yield return child;
            yield break;
        }
        var green = node.Green;
        var position = node.Position;
        for (var i = 0; i < green.SlotCount; i++)
        {
            if (green.GetSlot(i) is not { } slot)
                continue;
            if (slot is GreenToken token)
            {
                yield return new SyntaxNodeOrToken(new SyntaxToken(node.ChildParent, token, position));
            }
            else if (SyntaxNode.IsList(slot))
            {
                foreach (var inner in new ChildSyntaxList(node.SlotRed(i)!))
                    yield return inner;
            }
            else
            {
                yield return new SyntaxNodeOrToken(node.SlotRed(i)!);
            }
            position += slot.FullWidth;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
