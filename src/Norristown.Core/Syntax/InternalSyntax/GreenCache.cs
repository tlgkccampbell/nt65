using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Shares tokens and whitespace that recur often (mnemonics, registers, punctuation with
/// common trivia), as Roslyn's green-node cache does.
/// <para>
/// Each thread keeps its own tables. They are small and direct-mapped, so one shared set
/// would have threads lexing different files evicting each other, and the whitespace table
/// — which every indented line reaches — would churn worst of all. A token's slot is hashed
/// from the identity of its trivia list, so a lost whitespace entry takes every token that
/// was sharing that list with it. Sharing across threads would save one token object per
/// thread and cost all of that, so it is not worth having; the tables also need no locks
/// this way, and what the cache holds never depends on what another thread is doing.
/// </para>
/// </summary>
internal static class GreenCache
{
    private const int MaxText = 16;
    private const int TokenBits = 14;
    private const int WhitespaceBits = 8;

    [ThreadStatic]
    private static GreenToken?[]? tokenTable;

    [ThreadStatic]
    private static GreenTrivia[]?[]? whitespaceTable;

    private static GreenToken?[] Tokens => tokenTable ??= new GreenToken?[1 << TokenBits];

    private static GreenTrivia[]?[] Whitespaces => whitespaceTable ??= new GreenTrivia[]?[1 << WhitespaceBits];

    /// <summary>A trivia list holding one whitespace trivia; equal short texts share one array.</summary>
    public static ImmutableArray<GreenTrivia> Whitespace(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return ImmutableArray<GreenTrivia>.Empty;
        if (text.Length > MaxText)
            return [new GreenTrivia(SyntaxKind.WhitespaceTrivia, text.ToString())];
        var table = Whitespaces;
        var slot = string.GetHashCode(text) & ((1 << WhitespaceBits) - 1);
        if (table[slot] is { } hit && hit[0].Text.AsSpan().SequenceEqual(text))
            return ImmutableCollectionsMarshal.AsImmutableArray(hit);
        var created = new[] { new GreenTrivia(SyntaxKind.WhitespaceTrivia, text.ToString()) };
        table[slot] = created;
        return ImmutableCollectionsMarshal.AsImmutableArray(created);
    }

    /// <summary>
    /// A token, shared when it has no error, short text and trivia from <see cref="Whitespace"/>
    /// (or none). Trivia lists are compared by reference, which is exact for shared lists.
    /// </summary>
    public static GreenToken Token(SyntaxKind kind, ReadOnlySpan<char> text, ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing, IReadOnlyList<DiagnosticMessage>? errors)
    {
        if (errors is not null || text.Length > MaxText || !Shared(leading) || !Shared(trailing))
            return new GreenToken(kind, text.ToString(), leading, trailing, errors);

        var leadingArray = ImmutableCollectionsMarshal.AsArray(leading);
        var trailingArray = ImmutableCollectionsMarshal.AsArray(trailing);
        var hash = HashCode.Combine(kind, string.GetHashCode(text),
            RuntimeHelpers.GetHashCode(leadingArray), RuntimeHelpers.GetHashCode(trailingArray));
        var table = Tokens;
        var slot = hash & ((1 << TokenBits) - 1);
        if (table[slot] is { } hit && hit.Kind == kind && hit.Text.AsSpan().SequenceEqual(text)
            && ImmutableCollectionsMarshal.AsArray(hit.LeadingTrivia) == leadingArray
            && ImmutableCollectionsMarshal.AsArray(hit.TrailingTrivia) == trailingArray)
        {
            return hit;
        }
        var created = new GreenToken(kind, text.ToString(), leading, trailing, null);
        table[slot] = created;
        return created;
    }

    private static bool Shared(ImmutableArray<GreenTrivia> trivia) =>
        trivia.IsEmpty || (trivia.Length == 1 && trivia[0].Kind == SyntaxKind.WhitespaceTrivia && trivia[0].Text.Length <= MaxText);
}
