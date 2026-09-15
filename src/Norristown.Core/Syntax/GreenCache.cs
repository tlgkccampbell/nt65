using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Norristown.Syntax;

/// <summary>
/// Shares tokens and whitespace that recur often (mnemonics, registers, punctuation with
/// common trivia), as Roslyn's green-node cache does. Fixed-size tables with no locks:
/// entries are immutable, so a race only loses an entry.
/// </summary>
internal static class GreenCache
{
    private const int MaxText = 16;
    private const int TokenBits = 14;
    private const int WhitespaceBits = 8;

    private static readonly GreenToken?[] tokens = new GreenToken?[1 << TokenBits];
    private static readonly GreenTrivia[]?[] whitespace = new GreenTrivia[]?[1 << WhitespaceBits];

    /// <summary>A trivia list holding one whitespace trivia; equal short texts share one array.</summary>
    public static ImmutableArray<GreenTrivia> Whitespace(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return ImmutableArray<GreenTrivia>.Empty;
        if (text.Length > MaxText)
            return [new GreenTrivia(SyntaxKind.WhitespaceTrivia, text.ToString())];
        var slot = string.GetHashCode(text) & ((1 << WhitespaceBits) - 1);
        if (whitespace[slot] is { } hit && hit[0].Text.AsSpan().SequenceEqual(text))
            return ImmutableCollectionsMarshal.AsImmutableArray(hit);
        var created = new[] { new GreenTrivia(SyntaxKind.WhitespaceTrivia, text.ToString()) };
        whitespace[slot] = created;
        return ImmutableCollectionsMarshal.AsImmutableArray(created);
    }

    /// <summary>
    /// A token, shared when it has no error, short text and trivia from <see cref="Whitespace"/>
    /// (or none). Trivia lists are compared by reference, which is exact for shared lists.
    /// </summary>
    public static GreenToken Token(SyntaxKind kind, ReadOnlySpan<char> text, ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing, string? error)
    {
        if (error is not null || text.Length > MaxText || !Shared(leading) || !Shared(trailing))
            return new GreenToken(kind, text.ToString(), leading, trailing, error);

        var leadingArray = ImmutableCollectionsMarshal.AsArray(leading);
        var trailingArray = ImmutableCollectionsMarshal.AsArray(trailing);
        var hash = HashCode.Combine(kind, string.GetHashCode(text),
            RuntimeHelpers.GetHashCode(leadingArray), RuntimeHelpers.GetHashCode(trailingArray));
        var slot = hash & ((1 << TokenBits) - 1);
        if (tokens[slot] is { } hit && hit.Kind == kind && hit.Text.AsSpan().SequenceEqual(text)
            && ImmutableCollectionsMarshal.AsArray(hit.LeadingTrivia) == leadingArray
            && ImmutableCollectionsMarshal.AsArray(hit.TrailingTrivia) == trailingArray)
        {
            return hit;
        }
        var created = new GreenToken(kind, text.ToString(), leading, trailing, null);
        tokens[slot] = created;
        return created;
    }

    private static bool Shared(ImmutableArray<GreenTrivia> trivia) =>
        trivia.IsEmpty || (trivia.Length == 1 && trivia[0].Kind == SyntaxKind.WhitespaceTrivia && trivia[0].Text.Length <= MaxText);
}
