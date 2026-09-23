using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// A token with its parent and absolute position. It is a three-field value, copied rather than
/// allocated, and two are equal when they are the same token of the same tree reached through
/// the same parent node. Only a tree hands out real tokens: a default <see cref="SyntaxToken"/>
/// belongs to nothing, and is what a lookup returns when there is no token to return.
/// </summary>
public readonly record struct SyntaxToken
{
    /// <summary>Wraps <paramref name="green"/> as a token whose parent is <paramref name="parent"/>.</summary>
    /// <param name="parent">The node the token is a piece of.</param>
    /// <param name="green">The green token it wraps.</param>
    /// <param name="position">Where it starts in the file's text, trivia included.</param>
    internal SyntaxToken(SyntaxNode parent, GreenToken green, int position)
    {
        Parent = parent;
        Green = green;
        Position = position;
    }

    /// <summary>
    /// The node the token is part of; for a token read from <see cref="LineSyntax.Tokens"/>,
    /// that is the line.
    /// </summary>
    public SyntaxNode Parent { get; }

    /// <summary>Where the token starts in the file's text, trivia included.</summary>
    public int Position { get; }

    /// <summary>What the token is.</summary>
    public SyntaxKind Kind => Green.Kind;

    /// <summary>The token's text, exactly as in the source.</summary>
    public string Text => Green.Text;

    /// <summary>
    /// The instruction a <see cref="SyntaxKind.Mnemonic"/> token names, whatever case it is
    /// written in, and <see cref="Syntax.MnemonicKind.None"/> for every other token.
    /// </summary>
    public MnemonicKind MnemonicKind => Green.MnemonicKind;

    /// <summary>
    /// Whether the token fills a place the syntax requires but the source does not write. Its
    /// text is empty and its span is the empty span where it would have been written.
    /// </summary>
    public bool IsMissing => Green.IsMissing;

    /// <summary>
    /// Whether the token carries a diagnostic: a lexical error over its text, or, on a missing
    /// token, a report of what the line needed in its place.
    /// </summary>
    public bool ContainsDiagnostics => Green.ContainsDiagnostics;

    /// <summary>Whether the token carries a <see cref="SyntaxAnnotation"/>.</summary>
    public bool ContainsAnnotations => Green.ContainsAnnotations;

    /// <summary>The token's range, without trivia.</summary>
    public TextSpan Span => new(Position + Green.LeadingWidth, Green.Text.Length);

    /// <summary>The token's range including its trivia.</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>The whitespace before the token; only the first token on a line has any.</summary>
    public SyntaxTriviaList LeadingTrivia => new(this, Green.LeadingTrivia, Position);

    /// <summary>The whitespace and comment after the token, up to the end of its line.</summary>
    public SyntaxTriviaList TrailingTrivia => new(this, Green.TrailingTrivia, Span.End);

    /// <summary>The green token this one wraps.</summary>
    internal GreenToken Green { get; }

    /// <summary>The token's text with its trivia, exactly as in the source.</summary>
    public string ToFullString() => Green.ToFullString();

    /// <summary>
    /// The token written after this one, wherever it is: the next token of this line, or the
    /// first of the line below, and null at the end of the file. It walks the same tokens as
    /// <see cref="SyntaxNode.DescendantTokens"/>, missing ones included, and each of them belongs
    /// to the node it is part of.
    /// </summary>
    public SyntaxToken? GetNextToken() => Step(1);

    /// <summary>
    /// The token written before this one, wherever it is: the token before it on this line, or
    /// the last of the line above, and null at the start of the file.
    /// </summary>
    public SyntaxToken? GetPreviousToken() => Step(-1);

    /// <summary>
    /// This token written as <paramref name="text"/>, keeping its kind and the trivia around it,
    /// or this token itself when the text is unchanged. This is what a rename does: the same
    /// token of the same line, spelled another way.
    /// </summary>
    /// <param name="text">What the token is to say.</param>
    /// <returns>The token, which belongs to no file until a rewrite puts it into one.</returns>
    public SyntaxToken WithText(string text) =>
        text == Text ? this : Rebuilt(text, Green.LeadingTrivia, Green.TrailingTrivia);

    /// <summary>This token with <paramref name="trivia"/> before it.</summary>
    /// <param name="trivia">The whitespace to write before the token.</param>
    /// <returns>The token, which belongs to no file until a rewrite puts it into one.</returns>
    public SyntaxToken WithLeadingTrivia(params IEnumerable<SyntaxTrivia> trivia) =>
        Rebuilt(Text, Trivia(trivia), Green.TrailingTrivia);

    /// <summary>This token with <paramref name="trivia"/> after it.</summary>
    /// <param name="trivia">The whitespace and comments to write after the token.</param>
    /// <returns>The token, which belongs to no file until a rewrite puts it into one.</returns>
    public SyntaxToken WithTrailingTrivia(params IEnumerable<SyntaxTrivia> trivia) =>
        Rebuilt(Text, Green.LeadingTrivia, Trivia(trivia));

    /// <summary>
    /// This token with the trivia of <paramref name="other"/>: its own kind and text, and the
    /// trivia <paramref name="other"/> carries. A fix that swaps one token for another uses it,
    /// so that the indentation and the comment on the line stay where they were.
    /// </summary>
    /// <param name="other">The token being written over.</param>
    /// <returns>The token, which belongs to no file until a rewrite puts it into one.</returns>
    public SyntaxToken WithTriviaFrom(SyntaxToken other) =>
        Rebuilt(Text, other.Green.LeadingTrivia, other.Green.TrailingTrivia);

    /// <summary>
    /// This token carrying <paramref name="annotations"/> as well as the ones it has. It is a
    /// <em>different</em> token from this one, belonging to no file until a rewrite puts it into
    /// one, which is what lets a rewrite find it again afterwards.
    /// </summary>
    /// <param name="annotations">The annotations to put on, which it does not already carry.</param>
    /// <returns>This token, or the token it has become.</returns>
    public SyntaxToken WithAdditionalAnnotations(params IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Green.Annotations;
        var wanted = own.AddRange(annotations.Where(annotation => !own.Contains(annotation)).Distinct());
        return wanted.Length == own.Length ? this : Carrying(wanted);
    }

    /// <summary>This token without <paramref name="annotations"/>, and with the rest of its own.</summary>
    /// <param name="annotations">The annotations to take off.</param>
    /// <returns>This token, or the token it has become.</returns>
    public SyntaxToken WithoutAnnotations(params IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Green.Annotations;
        var kept = own.RemoveRange(annotations);
        return kept.Length == own.Length ? this : Carrying(kept);
    }

    /// <summary>This token without the annotations of <paramref name="kind"/> it carries.</summary>
    /// <param name="kind">The kind of annotation to take off.</param>
    /// <returns>This token, or the token it has become.</returns>
    public SyntaxToken WithoutAnnotations(string kind)
    {
        var own = Green.Annotations;
        var kept = own.RemoveAll(annotation => annotation.Kind == kind);
        return kept.Length == own.Length ? this : Carrying(kept);
    }

    /// <summary>Whether this token carries <paramref name="annotation"/>.</summary>
    /// <param name="annotation">The annotation to look for, matched by reference.</param>
    public bool HasAnnotation(SyntaxAnnotation annotation) => Green.Annotations.Contains(annotation);

    /// <summary>Whether this token carries an annotation of <paramref name="kind"/>.</summary>
    /// <param name="kind">The kind to look for.</param>
    public bool HasAnnotations(string kind) => GetAnnotations(kind).Any();

    /// <summary>The annotations of <paramref name="kind"/> on this token, in the order they were put on.</summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxAnnotation> GetAnnotations(string kind) =>
        Green.Annotations.Where(annotation => annotation.Kind == kind);

    /// <summary>The syntax diagnostics on this token, in source order.</summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics()
    {
        var result = new List<Diagnostic>();
        Parent.Tree.Collect(Green, Position, result);
        return result;
    }

    /// <summary>The token's text, without trivia.</summary>
    public override string ToString() => Text;

    /// <summary>The green trivia of <paramref name="trivia"/>, in the order it is written.</summary>
    private static ImmutableArray<GreenTrivia> Trivia(IEnumerable<SyntaxTrivia> trivia) =>
        [.. trivia.Select(one => one.Green)];

    /// <summary>This token carrying <paramref name="wanted"/> in place of the annotations it has.</summary>
    private SyntaxToken Carrying(ImmutableArray<SyntaxAnnotation> wanted) =>
        SyntaxFactory.Detached((GreenToken)Green.WithAnnotations(wanted));

    /// <summary>
    /// This token rebuilt with new text or trivia. A token's diagnostics are about the text it was
    /// lexed from, so a rebuilt token carries none: no source has been lexed with its text yet.
    /// Its annotations are not about its text, so they are kept: the same token of the same line,
    /// spelled another way, is still the token that was tagged.
    /// </summary>
    private SyntaxToken Rebuilt(string text, ImmutableArray<GreenTrivia> leading, ImmutableArray<GreenTrivia> trailing)
    {
        if (Green.IsMissing && text.Length == 0)
            return this;
        var green = new GreenToken(Kind, text, leading, trailing, null);
        return green.ToFullString() == Green.ToFullString() && green.Kind == Green.Kind && !Green.ContainsDiagnostics
            ? this
            : SyntaxFactory.Detached((GreenToken)green.WithAnnotations(Green.Annotations));
    }

    /// <summary>
    /// Whether <paramref name="token"/> is the same green token, at the same position, as
    /// <paramref name="sought"/>.
    /// </summary>
    private static bool Written(SyntaxToken token, SyntaxToken sought) =>
        token.Position == sought.Position && ReferenceEquals(token.Green, sought.Green);

    /// <summary>
    /// The token one step along from this one, forwards or backwards. The search works one line
    /// at a time: a line has few enough tokens to walk them all, and stepping past either end of
    /// a line gives the first token of the next line or the last token of the previous one.
    /// </summary>
    /// <param name="direction">1 for the token after this one, −1 for the one before it.</param>
    private SyntaxToken? Step(int direction)
    {
        // A token inside a statement has one of the statement's nodes as its parent, so walk up
        // the parents to find the line it is written on.
        var owner = Parent;
        while (owner is not LineSyntax && owner.Parent is { } outer)
            owner = outer;

        // Two missing pieces can sit at the same position with nothing between them — `f(g(1`
        // is missing two `)` — so identifying a token takes its parent as well as its position.
        // A token read from the line's Tokens rather than from its pieces has the line as its
        // parent instead, so when nothing matches by parent, match by position alone.
        var self = this;
        var (found, beside) = Beside(owner, direction,
            token => Written(token, self) && ReferenceEquals(token.Parent, self.Parent));
        if (!found)
            (found, beside) = Beside(owner, direction, token => Written(token, self));
        if (!found)
            return null;
        if (beside is { } neighbour)
            return neighbour;
        if (owner is not LineSyntax line)
            return null;

        var next = line.LineIndex + direction;
        if (next < 0 || next >= line.Tree.LineCount)
            return null;
        SyntaxToken? edge = null;
        foreach (var token in line.Tree.GetLine(next).DescendantTokens())
        {
            edge = token;
            if (direction > 0)
                break;
        }
        return edge;
    }

    /// <summary>
    /// The neighbour of the token that <paramref name="chosen"/> picks out among
    /// <paramref name="owner"/>'s tokens, found in one pass without building a list, since an
    /// editor asks for a token's neighbour very often.
    /// </summary>
    /// <param name="owner">The node whose tokens to walk, which is a line.</param>
    /// <param name="direction">1 for the token after the chosen one, −1 for the one before it.</param>
    /// <param name="chosen">Which token to find the neighbour of.</param>
    /// <returns>
    /// Whether the chosen token was among them, and its neighbour, which is null where the
    /// chosen token is the first or the last of them.
    /// </returns>
    private static (bool Found, SyntaxToken? Beside) Beside(
        SyntaxNode owner, int direction, Func<SyntaxToken, bool> chosen)
    {
        SyntaxToken? previous = null;
        var after = false;
        foreach (var token in owner.DescendantTokens())
        {
            if (after)
                return (true, token);
            if (chosen(token))
            {
                if (direction < 0)
                    return (true, previous);
                after = true;
            }
            previous = token;
        }
        return (after, null);
    }
}
