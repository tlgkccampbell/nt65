using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// The items of a list-valued child, with nothing between them: a file's or a block's
/// members. The list is a view over the node the items hang from, so asking a node for one
/// costs nothing, and the red item nodes are made on first use and kept on that node.
/// <para>
/// A default list is the empty one, which is what a slot with nothing in it reads as.
/// </para>
/// </summary>
/// <typeparam name="T">What the items are.</typeparam>
public readonly struct SyntaxList<T> : IReadOnlyList<T> where T : SyntaxNode
{
    private readonly SyntaxNode? list;

    internal SyntaxList(SyntaxNode? list) => this.list = list;

    /// <summary>How many items the list has.</summary>
    public int Count => list?.Green.SlotCount ?? 0;

    /// <summary>The item at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return (T)list!.ChildNodes[index];
        }
    }

    /// <summary>The green list the items hang from, or null for a list with nothing in it.</summary>
    internal GreenList? Green => list?.Green as GreenList;

    /// <summary>The <paramref name="length"/> items from <paramref name="start"/>, which is what a slice pattern reads.</summary>
    /// <param name="start">The first item to take.</param>
    /// <param name="length">How many to take.</param>
    public ImmutableArray<T> Slice(int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<T>(length);
        for (var i = 0; i < length; i++)
            builder.Add(this[start + i]);
        return builder.MoveToImmutable();
    }

    /// <summary>Walks the items in source order.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
