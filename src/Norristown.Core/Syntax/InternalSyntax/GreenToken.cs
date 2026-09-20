using System.Collections.Immutable;
using System.Text;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>One token and the trivia around it.</summary>
public sealed class GreenToken : GreenNode
{
    // The missing token of each kind, which every node that wants one shares: a missing token
    // has no text and no trivia of its own, so one per kind is all there is to have. They are
    // held here and nowhere else, so the lexer's cache can never hand one out for a token the
    // source really wrote. A kind is a byte, hence the size.
    private static readonly GreenToken?[] missing = new GreenToken?[byte.MaxValue + 1];

    internal GreenToken(SyntaxKind kind, string text, ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing, string? error)
        : base(kind, TriviaWidth(leading) + text.Length + TriviaWidth(trailing))
    {
        Text = text;
        LeadingTrivia = leading;
        TrailingTrivia = trailing;
        Error = error;
    }

    private GreenToken(SyntaxKind kind) : base(kind, 0)
    {
        Text = "";
        LeadingTrivia = [];
        TrailingTrivia = [];
        IsMissing = true;
    }

    /// <summary>The token's text, exactly as in the source.</summary>
    public string Text { get; }

    /// <summary>Whitespace before the token; only the first token on a line has any.</summary>
    public ImmutableArray<GreenTrivia> LeadingTrivia { get; }

    /// <summary>Whitespace and a comment after the token, up to the end of its line.</summary>
    public ImmutableArray<GreenTrivia> TrailingTrivia { get; }

    /// <summary>A lexical error covering the token's text, or null.</summary>
    public string? Error { get; }

    /// <summary>
    /// Whether the token stands where one belongs that the source does not have. It has no
    /// text and no trivia, so it is nowhere in the file's text and takes up no width.
    /// </summary>
    public override bool IsMissing { get; }

    /// <summary>Width of the leading trivia, which is where the token's own text starts.</summary>
    public int LeadingWidth => TriviaWidth(LeadingTrivia);

    /// <summary>Always 0: a token has no children.</summary>
    public override int SlotCount => 0;

    /// <summary>A token has no children, so this always throws.</summary>
    public override GreenNode? GetSlot(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>The token's text, without trivia.</summary>
    public override string ToString() => Text;

    /// <summary>
    /// The missing token of <paramref name="kind"/>, for a piece a line needs and does not
    /// have. One instance per kind is shared, since a missing token holds nothing of its own.
    /// </summary>
    internal static GreenToken Missing(SyntaxKind kind)
    {
        var index = (int)kind;
        if (missing[index] is { } shared)
            return shared;
        var created = new GreenToken(kind);
        return Interlocked.CompareExchange(ref missing[index], created, null) ?? created;
    }

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        throw new InvalidOperationException("a token has no red node: it is a SyntaxToken of its parent");

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
}
