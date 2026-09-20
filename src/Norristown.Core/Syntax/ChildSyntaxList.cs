using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// A node's children, nodes and tokens together, in source order: one child per slot of the
/// green node. The list itself is a view over the node, so asking for it costs nothing; the
/// red node of a child that is a node is the one the parent keeps, made when first asked for.
/// </summary>
public readonly struct ChildSyntaxList : IEnumerable<SyntaxNodeOrToken>
{
    private readonly SyntaxNode? node;

    internal ChildSyntaxList(SyntaxNode node) => this.node = node;

    /// <summary>How many children the node has.</summary>
    public int Count => node?.Green.SlotCount ?? 0;

    /// <summary>The child at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
    public SyntaxNodeOrToken this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            var green = node!.Green;
            int position = node.Position, nodes = 0;
            for (var i = 0; i < index; i++)
            {
                var earlier = green.GetSlot(i);
                if (earlier is not GreenToken)
                    nodes++;
                position += earlier.FullWidth;
            }
            return green.GetSlot(index) is GreenToken token
                ? new SyntaxNodeOrToken(new SyntaxToken(node, token, position))
                : new SyntaxNodeOrToken(node.ChildNodes[nodes]);
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
        int position = node.Position, nodes = 0;
        for (var i = 0; i < green.SlotCount; i++)
        {
            var slot = green.GetSlot(i);
            yield return slot is GreenToken token
                ? new SyntaxNodeOrToken(new SyntaxToken(node, token, position))
                : new SyntaxNodeOrToken(node.ChildNodes[nodes++]);
            position += slot.FullWidth;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
