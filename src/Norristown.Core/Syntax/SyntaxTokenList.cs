using System.Collections;
using System.Collections.Immutable;

namespace Norristown.Syntax;

/// <summary>
/// The tokens of a node whose children are all tokens: the tokens left over at the end of a
/// line, or the whole of a line the parser could not read. The list is a view over that node,
/// so asking for it costs nothing.
/// <para>
/// A default list is the empty one, which is what a slot with nothing in it reads as.
/// </para>
/// </summary>
public readonly struct SyntaxTokenList : IReadOnlyList<SyntaxToken>
{
    private readonly SyntaxNode? list;

    internal SyntaxTokenList(SyntaxNode? list) => this.list = list;

    /// <summary>How many tokens the list has.</summary>
    public int Count => list?.Green.SlotCount ?? 0;

    /// <summary>The token at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
    public SyntaxToken this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return list!.ChildTokens[index];
        }
    }

    /// <summary>The <paramref name="length"/> tokens from <paramref name="start"/>, which is what a slice pattern reads.</summary>
    /// <param name="start">The first token to take.</param>
    /// <param name="length">How many to take.</param>
    public ImmutableArray<SyntaxToken> Slice(int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxToken>(length);
        for (var i = 0; i < length; i++)
            builder.Add(this[start + i]);
        return builder.MoveToImmutable();
    }

    /// <summary>Walks the tokens in source order.</summary>
    public IEnumerator<SyntaxToken> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
