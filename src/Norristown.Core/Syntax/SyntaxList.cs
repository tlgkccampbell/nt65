using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents the items of a list-valued child that has no separators, such as the members of a
/// file or a block. The list is a view over the node that holds the items, so getting a list from
/// a node costs nothing. The red item nodes are created on first use and cached on that node.
/// <para>
/// A default list is empty, and an empty slot is read as a default list.
/// </para>
/// </summary>
/// <typeparam name="T">The type of the items.</typeparam>
public readonly struct SyntaxList<T> : IReadOnlyList<T> where T : SyntaxNode
{
    private readonly SyntaxNode? list;

    internal SyntaxList(SyntaxNode? list) => this.list = list;

    /// <summary>Gets the number of items in the list.</summary>
    public int Count => list?.Green.SlotCount ?? 0;

    /// <summary>Gets the item at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return (T)list!.ChildNodes[index];
        }
    }

    /// <summary>Gets the green list that holds the items, or null if the list is empty.</summary>
    internal GreenList? Green => list?.Green as GreenList;

    /// <summary>
    /// Returns the <paramref name="length"/> items starting at <paramref name="start"/>. C# slice
    /// patterns call this method.
    /// </summary>
    /// <param name="start">The index of the first item to take.</param>
    /// <param name="length">The number of items to take.</param>
    public ImmutableArray<T> Slice(int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<T>(length);
        for (var i = 0; i < length; i++)
            builder.Add(this[start + i]);
        return builder.MoveToImmutable();
    }

    /// <summary>Returns an enumerator over the items in source order.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
