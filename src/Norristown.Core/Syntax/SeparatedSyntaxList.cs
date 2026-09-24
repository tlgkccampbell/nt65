using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents the items of a list that has a separator between items, nearly always a comma,
/// together with the separators. Examples are the arguments of a call, the items of an
/// <c>.export</c> and the values of a data line. An editor needs the separators as much as the
/// items, for example to add an argument, delete an item with its comma, or count commas up to
/// the caret. <see cref="GetSeparator"/> and <see cref="GetWithSeparators"/> provide them.
/// <para>
/// The list is a view over the node that holds the items, so getting a list from a node costs
/// nothing. A default list is empty, and an empty slot is read as a default list.
/// </para>
/// </summary>
/// <typeparam name="T">The type of the items.</typeparam>
public readonly struct SeparatedSyntaxList<T> : IReadOnlyList<T> where T : SyntaxNode
{
    private readonly SyntaxNode? list;

    internal SeparatedSyntaxList(SyntaxNode? list) => this.list = list;

    /// <summary>Gets the number of items in the list, not counting separators.</summary>
    public int Count => (Slots + 1) / 2;

    /// <summary>
    /// Gets the number of separators. This is one fewer than the number of items, or the same
    /// number if the source ends the list with a separator.
    /// </summary>
    public int SeparatorCount => Slots / 2;

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
    internal GreenSeparatedList? Green => list?.Green as GreenSeparatedList;

    private int Slots => list?.Green.SlotCount ?? 0;

    /// <summary>
    /// Returns the separator after item <paramref name="index"/>, from 0 to
    /// <see cref="SeparatorCount"/> − 1.
    /// </summary>
    /// <param name="index">The index of the separator.</param>
    public SyntaxToken GetSeparator(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SeparatorCount);
        return list!.ChildTokens[index];
    }

    /// <summary>Returns every separator, in source order.</summary>
    public ImmutableArray<SyntaxToken> GetSeparators()
    {
        var count = SeparatorCount;
        var builder = ImmutableArray.CreateBuilder<SyntaxToken>(count);
        for (var i = 0; i < count; i++)
            builder.Add(GetSeparator(i));
        return builder.MoveToImmutable();
    }

    /// <summary>Returns the items and the separators together, in source order.</summary>
    public ChildSyntaxList GetWithSeparators() => list is null ? default : new(list);

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

    /// <summary>Returns an enumerator over the items in source order, without the separators.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
