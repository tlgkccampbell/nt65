using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents the tokens of a node whose slots are all tokens. Examples are a line's own tokens
/// as the lexer read them, the tokens left over at the end of a line, and the whole of a line the
/// parser could not read. The list is a view over that node and creates each token on request, so
/// neither getting the list nor walking it allocates anything.
/// <para>
/// A default list is empty, and an empty slot is read as a default list.
/// </para>
/// </summary>
public readonly struct SyntaxTokenList : IReadOnlyList<SyntaxToken>
{
    private readonly SyntaxNode? list;

    internal SyntaxTokenList(SyntaxNode? list) => this.list = list;

    /// <summary>Gets the number of tokens in the list.</summary>
    public int Count => list?.Green.SlotCount ?? 0;

    /// <summary>Gets the token at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
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
    /// Gets the green list that holds the tokens, or null if the tokens are not a list of their
    /// own. That happens for an empty slot, and for a line, whose tokens are its own slots.
    /// </summary>
    internal GreenList? Green => list?.Green as GreenList;

    /// <summary>
    /// Returns the <paramref name="length"/> tokens starting at <paramref name="start"/>. C# slice
    /// patterns call this method.
    /// </summary>
    /// <param name="start">The index of the first token to take.</param>
    /// <param name="length">The number of tokens to take.</param>
    public ImmutableArray<SyntaxToken> Slice(int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxToken>(length);
        for (var i = 0; i < length; i++)
            builder.Add(this[start + i]);
        return builder.MoveToImmutable();
    }

    /// <summary>Returns an enumerator over the tokens in source order.</summary>
    /// <returns>An enumerator that allocates nothing, which a <c>foreach</c> uses.</returns>
    public Enumerator GetEnumerator() => new(list);

    IEnumerator<SyntaxToken> IEnumerable<SyntaxToken>.GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<SyntaxToken>)this).GetEnumerator();

    /// <summary>
    /// Enumerates a list's tokens while tracking the position reached, so that walking a whole line
    /// costs one pass over its slots rather than a sum of widths per token.
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

        /// <summary>Gets the token at the enumerator's current position.</summary>
        public SyntaxToken Current { get; private set; }

        /// <summary>Advances to the next token.</summary>
        /// <returns>true if there was a next token; otherwise, false.</returns>
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
