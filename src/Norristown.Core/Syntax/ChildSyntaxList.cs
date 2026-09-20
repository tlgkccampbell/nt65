using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// A node's children, nodes and tokens together, in source order. The list itself is a view
/// over the node, so asking for it costs nothing; the red node of a child that is a node is
/// the one the parent keeps, made when first asked for.
/// <para>
/// A slot holding nothing shows no child, and a slot holding a list shows the list's items and
/// separators rather than the node over them, as Roslyn's does: an analyzer walking a node's
/// children never meets a list node.
/// </para>
/// </summary>
public readonly struct ChildSyntaxList : IEnumerable<SyntaxNodeOrToken>
{
    private readonly SyntaxNode? node;

    internal ChildSyntaxList(SyntaxNode node) => this.node = node;

    /// <summary>How many children the node has.</summary>
    public int Count
    {
        get
        {
            if (node is null)
                return 0;
            var count = 0;
            for (var i = 0; i < node.Green.SlotCount; i++)
            {
                if (node.Green.GetSlot(i) is { } slot)
                    count += SyntaxNode.IsList(slot) ? new ChildSyntaxList(node.SlotRed(i)!).Count : 1;
            }
            return count;
        }
    }

    /// <summary>The child at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
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

    /// <summary>The <paramref name="length"/> children from <paramref name="start"/>, which is what a slice pattern reads.</summary>
    /// <param name="start">The first child to take.</param>
    /// <param name="length">How many to take.</param>
    public ImmutableArray<SyntaxNodeOrToken> Slice(int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxNodeOrToken>(length);
        for (var i = 0; i < length; i++)
            builder.Add(this[start + i]);
        return builder.MoveToImmutable();
    }

    /// <summary>Walks the children in source order.</summary>
    public IEnumerator<SyntaxNodeOrToken> GetEnumerator()
    {
        if (node is null)
            yield break;
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
