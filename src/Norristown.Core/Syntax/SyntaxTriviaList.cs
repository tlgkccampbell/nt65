using System.Collections;
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents the trivia on one side of a token, in source order. This is either the whitespace
/// before the token, or the whitespace and comment after it. The list is a view over the token, so
/// reading <see cref="SyntaxToken.LeadingTrivia"/> or <see cref="SyntaxToken.TrailingTrivia"/>
/// costs nothing until the trivia itself is requested.
/// </summary>
public readonly struct SyntaxTriviaList : IEnumerable<SyntaxTrivia>
{
    private readonly SyntaxToken token;
    private readonly ImmutableArray<GreenTrivia> trivia;
    private readonly int position;

    internal SyntaxTriviaList(SyntaxToken token, ImmutableArray<GreenTrivia> trivia, int position)
    {
        this.token = token;
        this.trivia = trivia;
        this.position = position;
    }

    /// <summary>Gets the token that the trivia belongs to.</summary>
    public SyntaxToken Token => token;

    /// <summary>Gets the number of trivia items in the list.</summary>
    public int Count => trivia.IsDefaultOrEmpty ? 0 : trivia.Length;

    /// <summary>Gets the trivia at <paramref name="index"/>, from 0 to <see cref="Count"/> − 1.</summary>
    public SyntaxTrivia this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            var start = position;
            for (var i = 0; i < index; i++)
                start += trivia[i].Text.Length;
            return new SyntaxTrivia(token, trivia[index], start);
        }
    }

    /// <summary>
    /// Returns the <paramref name="length"/> trivia items starting at <paramref name="start"/>. C#
    /// slice patterns call this method.
    /// </summary>
    /// <param name="start">The index of the first trivia item to take.</param>
    /// <param name="length">The number of trivia items to take.</param>
    public ImmutableArray<SyntaxTrivia> Slice(int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxTrivia>(length);
        for (var i = 0; i < length; i++)
            builder.Add(this[start + i]);
        return builder.MoveToImmutable();
    }

    /// <summary>Returns an enumerator over the trivia in source order.</summary>
    public IEnumerator<SyntaxTrivia> GetEnumerator()
    {
        var start = position;
        for (var i = 0; i < Count; i++)
        {
            yield return new SyntaxTrivia(token, trivia[i], start);
            start += trivia[i].Text.Length;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
