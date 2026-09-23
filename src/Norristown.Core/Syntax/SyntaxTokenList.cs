using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// The tokens of a node whose slots are all tokens: a line's own tokens as the lexer read them,
/// the tokens left over at the end of a line, or the whole of a line the parser could not read.
/// The list is a view over that node and makes each token as it is asked for, so neither asking
/// for the list nor walking it allocates anything.
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
            return list!.SlotToken(index);
        }
    }

    /// <summary>
    /// The green list the tokens hang from, or null where they are not a list of their own: an
    /// empty slot, or a line, whose tokens are its own slots.
    /// </summary>
    internal GreenList? Green => list?.Green as GreenList;

    /// <summary>The <paramref name="length"/> tokens from <paramref name="start"/>; C# slice patterns call this.</summary>
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
    /// <returns>A walk that allocates nothing, which is what a <c>foreach</c> uses.</returns>
    public Enumerator GetEnumerator() => new(list);

    IEnumerator<SyntaxToken> IEnumerable<SyntaxToken>.GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<SyntaxToken>)this).GetEnumerator();

    /// <summary>
    /// A walk over a list's tokens that carries the position it has reached, so that walking a
    /// whole line costs one pass over its slots rather than a sum of widths per token.
    /// </summary>
    public struct Enumerator
    {
        private readonly SyntaxNode? list;
        private int index;
        private int position;

        internal Enumerator(SyntaxNode? list)
        {
            this.list = list;
            index = -1;
            position = list?.Position ?? 0;
            Current = default;
        }

        /// <summary>The token the walk has reached.</summary>
        public SyntaxToken Current { get; private set; }

        /// <summary>Steps to the next token.</summary>
        /// <returns>Whether there was one.</returns>
        public bool MoveNext()
        {
            if (list is null || ++index >= list.Green.SlotCount)
                return false;
            var green = (GreenToken)list.Green.GetSlot(index)!;
            Current = new SyntaxToken(list.ChildParent, green, position);
            position += green.FullWidth;
            return true;
        }
    }
}
