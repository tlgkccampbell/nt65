using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Norristown.Syntax;

/// <summary>
/// An immutable node with a kind, a width and children, and no parent or position, so an
/// unchanged line keeps its node across edits wherever the edit moved it.
/// </summary>
public abstract class GreenNode(SyntaxKind kind, int fullWidth)
{
    public SyntaxKind Kind { get; } = kind;

    /// <summary>Width including trivia.</summary>
    public int FullWidth { get; } = fullWidth;

    public abstract int SlotCount { get; }

    public abstract GreenNode GetSlot(int index);

    /// <summary>The node's text, exactly as in the source.</summary>
    public string ToFullString()
    {
        var builder = new StringBuilder(FullWidth);
        WriteTo(builder);
        return builder.ToString();
    }

    internal virtual void WriteTo(StringBuilder builder)
    {
        for (var i = 0; i < SlotCount; i++)
            GetSlot(i).WriteTo(builder);
    }

    protected static int SumWidths<T>(ImmutableArray<T> nodes) where T : GreenNode
    {
        var width = 0;
        foreach (var node in nodes)
            width += node.FullWidth;
        return width;
    }
}

public sealed class GreenTrivia(SyntaxKind kind, string text)
{
    public SyntaxKind Kind { get; } = kind;
    public string Text { get; } = text;
}

public sealed class GreenToken : GreenNode
{
    public string Text { get; }
    public ImmutableArray<GreenTrivia> LeadingTrivia { get; }
    public ImmutableArray<GreenTrivia> TrailingTrivia { get; }

    /// <summary>A lexical error covering the token's text, or null.</summary>
    public string? Error { get; }

    internal GreenToken(SyntaxKind kind, string text, ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing, string? error)
        : base(kind, TriviaWidth(leading) + text.Length + TriviaWidth(trailing))
    {
        Text = text;
        LeadingTrivia = leading;
        TrailingTrivia = trailing;
        Error = error;
    }

    public int LeadingWidth => TriviaWidth(LeadingTrivia);

    public override int SlotCount => 0;

    public override GreenNode GetSlot(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    internal override void WriteTo(StringBuilder builder)
    {
        foreach (var trivia in LeadingTrivia)
            builder.Append(trivia.Text);
        builder.Append(Text);
        foreach (var trivia in TrailingTrivia)
            builder.Append(trivia.Text);
    }

    private static int TriviaWidth(ImmutableArray<GreenTrivia> trivia)
    {
        var width = 0;
        foreach (var t in trivia)
            width += t.Text.Length;
        return width;
    }

    public override string ToString() => Text;
}

/// <summary>
/// One source line. Stage 1 holds its tokens; the parser will give it structure. Its kind
/// and brace value come from its own tokens alone (§4).
/// </summary>
public sealed class GreenLine : GreenNode
{
    /// <summary>The line's tokens, always ending with an <see cref="SyntaxKind.EndOfLine"/> token.</summary>
    public ImmutableArray<GreenToken> Tokens { get; }

    public LineKind LineKind { get; }

    /// <summary>The line's last token is a <c>{</c> outside any open parenthesis.</summary>
    public bool Opens { get; }

    /// <summary>The line's first token is <c>}</c>.</summary>
    public bool Closes { get; }

    /// <summary>For a line that opens a block, the kind of block; otherwise <see cref="BlockKind.None"/>.</summary>
    public BlockKind OpensBlockKind { get; }

    internal GreenLine(ImmutableArray<GreenToken> tokens) : base(SyntaxKind.Line, SumWidths(tokens))
    {
        Tokens = tokens;
        LineKind = Lines.Classify(tokens);
        (Opens, Closes) = Lines.Braces(tokens);
        OpensBlockKind = Opens ? Lines.BlockKindOf(tokens, LineKind) : BlockKind.None;
    }

    /// <summary>+1, −1 or 0: the line's contribution to the block depth.</summary>
    public int BraceValue => (Opens ? 1 : 0) - (Closes ? 1 : 0);

    /// <summary>Offset of token <paramref name="index"/>'s text from the start of the line.</summary>
    public int TextOffset(int index)
    {
        var offset = 0;
        for (var i = 0; i < index; i++)
            offset += Tokens[i].FullWidth;
        return offset + Tokens[index].LeadingWidth;
    }

    public override int SlotCount => Tokens.Length;

    public override GreenNode GetSlot(int index) => Tokens[index];
}

/// <summary>
/// A block: its opener line, then its contents, then the closing <c>}</c> line when it has
/// one. A block closed by a continuation line (<c>} .else {</c>) has no closer: that line
/// is the opener of the next block.
/// </summary>
public sealed class GreenBlock : GreenNode
{
    public ImmutableArray<GreenNode> Children { get; }
    public bool HasCloser { get; }

    internal GreenBlock(ImmutableArray<GreenNode> children, bool hasCloser) : base(SyntaxKind.Block, SumWidths(children))
    {
        Children = children;
        HasCloser = hasCloser;
    }

    public BlockKind BlockKind => Opener.OpensBlockKind;

    public GreenLine Opener => (GreenLine)Children[0];

    public GreenLine? Closer => HasCloser ? (GreenLine)Children[^1] : null;

    public override int SlotCount => Children.Length;

    public override GreenNode GetSlot(int index) => Children[index];
}

public sealed class GreenFile(ImmutableArray<GreenNode> children) : GreenNode(SyntaxKind.File, SumWidths(children))
{
    public ImmutableArray<GreenNode> Children { get; } = children;

    public override int SlotCount => Children.Length;

    public override GreenNode GetSlot(int index) => Children[index];
}

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
