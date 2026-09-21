using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// The items of a list written with a separator between them, nearly always a comma, and the
/// separators themselves: the arguments of a call, the items of an <c>.export</c>, the values
/// of a data line. An editor needs the separators as much as the items — adding an argument,
/// deleting an item with its comma, counting commas up to the caret — so
/// <see cref="GetSeparator"/> and <see cref="GetWithSeparators"/> give them.
/// <para>
/// The list is a view over the node the items hang from, so asking a node for one costs
/// nothing. A default list is the empty one, which is what a slot with nothing in it reads as.
/// </para>
/// </summary>
/// <typeparam name="T">What the items are.</typeparam>
public readonly struct SeparatedSyntaxList<T> : IReadOnlyList<T> where T : SyntaxNode
{
    private readonly SyntaxNode? list;

    internal SeparatedSyntaxList(SyntaxNode? list) => this.list = list;

    /// <summary>How many items the list has, separators aside.</summary>
    public int Count => (Slots + 1) / 2;

    /// <summary>
    /// How many separators there are: one fewer than the items, or as many when the source
    /// ends the list with one.
    /// </summary>
    public int SeparatorCount => Slots / 2;

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
    internal GreenSeparatedList? Green => list?.Green as GreenSeparatedList;

    private int Slots => list?.Green.SlotCount ?? 0;

    /// <summary>The separator after item <paramref name="index"/>, from 0 to <see cref="SeparatorCount"/> − 1.</summary>
    /// <param name="index">Which separator.</param>
    public SyntaxToken GetSeparator(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SeparatorCount);
        return list!.ChildTokens[index];
    }

    /// <summary>Every separator, in source order.</summary>
    public ImmutableArray<SyntaxToken> GetSeparators()
    {
        var count = SeparatorCount;
        var builder = ImmutableArray.CreateBuilder<SyntaxToken>(count);
        for (var i = 0; i < count; i++)
            builder.Add(GetSeparator(i));
        return builder.MoveToImmutable();
    }

    /// <summary>The items and the separators together, in source order.</summary>
    public ChildSyntaxList GetWithSeparators() => list is null ? default : new(list);

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

    /// <summary>Walks the items in source order, separators aside.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
