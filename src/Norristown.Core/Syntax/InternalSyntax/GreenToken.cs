using System.Collections.Immutable;
using System.Text;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>Represents one token and the trivia around it.</summary>
internal sealed class GreenToken : GreenNode
{
    // One shared missing token per kind, for missing tokens that report no diagnostic. Such a
    // token has no text, no trivia and no diagnostic of its own, so a single instance per kind
    // serves every node that needs one. They are held here and not in the lexer's cache, so the
    // cache can never hand one out for a token that is actually in the source. A SyntaxKind fits
    // in a byte, hence the array's size.
    private static readonly GreenToken?[] missing = new GreenToken?[byte.MaxValue + 1];

    internal GreenToken(SyntaxKind kind, string text, ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing, IReadOnlyList<DiagnosticMessage>? errors)
        : base(kind, TriviaWidth(leading) + text.Length + TriviaWidth(trailing))
    {
        Text = text;
        LeadingTrivia = leading;
        TrailingTrivia = trailing;
        if (kind == SyntaxKind.Mnemonic)
            MnemonicKind = SyntaxFacts.MnemonicKindOf(text);
        else if (kind == SyntaxKind.Directive)
            DirectiveKind = SyntaxFacts.DirectiveKindOf(text);

        // A lexical error covers the token's text and is given to the constructor rather than
        // reported afterwards. That is safe because the cache never shares a token that has an
        // error, so the diagnostic belongs to this one occurrence. A literal can be wrong in more
        // than one way, and each error is reported separately.
        foreach (var error in errors ?? [])
            Report(new GreenDiagnostic(TriviaWidth(leading), text.Length, error));
    }

    private GreenToken(SyntaxKind kind, GreenDiagnostic? diagnostic) : base(kind, 0)
    {
        Text = "";
        LeadingTrivia = [];
        TrailingTrivia = [];
        IsMissing = true;
        if (diagnostic is not null)
            Report(diagnostic);
    }

    /// <summary>Gets the token's text, exactly as in the source.</summary>
    public string Text { get; }

    /// <summary>
    /// Gets the instruction a mnemonic token names, or <see cref="MnemonicKind.None"/> for every
    /// other token.
    /// </summary>
    public MnemonicKind MnemonicKind { get; }

    /// <summary>
    /// Gets the directive a directive token names, or <see cref="DirectiveKind.None"/> for every
    /// other token, including a directive token that names a built-in function or no directive.
    /// </summary>
    public DirectiveKind DirectiveKind { get; }

    /// <summary>Gets the whitespace before the token. Only the first token on a line has any.</summary>
    public ImmutableArray<GreenTrivia> LeadingTrivia { get; }

    /// <summary>Gets the whitespace and any comment after the token, up to the end of its line.</summary>
    public ImmutableArray<GreenTrivia> TrailingTrivia { get; }

    /// <summary>
    /// Gets a value indicating whether the token fills a place the grammar requires but the
    /// source does not contain. Such a token has no text and no trivia, so it is nowhere in the
    /// file's text and takes up no width.
    /// </summary>
    public override bool IsMissing { get; }

    /// <summary>Gets the width of the leading trivia, which is where the token's own text starts.</summary>
    public int LeadingWidth => TriviaWidth(LeadingTrivia);

    /// <summary>
    /// Gets the width of the trailing trivia, which is what stands between this token and the next.
    /// </summary>
    public int TrailingWidth => TriviaWidth(TrailingTrivia);

    /// <summary>Gets 0, because a token has no children.</summary>
    public override int SlotCount => 0;

    /// <summary>Always throws, because a token has no children.</summary>
    public override GreenNode? GetSlot(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Returns the token's text, without trivia.</summary>
    public override string ToString() => Text;

    /// <summary>
    /// Returns the missing token of <paramref name="kind"/>, for a piece a line needs and does not
    /// have. One instance per kind is shared, since such a token holds nothing of its own.
    /// </summary>
    internal static GreenToken Missing(SyntaxKind kind)
    {
        var index = (int)kind;
        if (missing[index] is { } shared)
            return shared;
        var created = new GreenToken(kind, null);
        return Interlocked.CompareExchange(ref missing[index], created, null) ?? created;
    }

    /// <summary>
    /// Returns a missing token of <paramref name="kind"/> that reports why it is missing. It is a
    /// new instance rather than the shared one, since its diagnostic applies only to the one place
    /// where it is used.
    /// </summary>
    /// <param name="kind">The kind of token the source does not have.</param>
    /// <param name="diagnostic">The diagnostic to report for the missing token, placed within the token.</param>
    internal static GreenToken Missing(SyntaxKind kind, GreenDiagnostic diagnostic) => new(kind, diagnostic);

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
