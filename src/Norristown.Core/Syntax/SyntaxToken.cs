using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents a token together with its parent and absolute position. It is a three-field value,
/// copied rather than allocated. Two tokens are equal when they are the same token of the same
/// tree. A token has one parent however it was reached, so the same token read from a line's
/// <see cref="LineSyntax.Tokens"/> or from a walk of the tree is equal. Only a tree hands out
/// real tokens. A default
/// <see cref="SyntaxToken"/> belongs to nothing, and a lookup returns it when there is no token
/// to return.
/// </summary>
public readonly record struct SyntaxToken
{
    /// <summary>Wraps <paramref name="green"/> as a token whose parent is <paramref name="parent"/>.</summary>
    /// <param name="parent">The node the token belongs to.</param>
    /// <param name="green">The green token to wrap.</param>
    /// <param name="position">The offset in the file's text where the token starts, trivia included.</param>
    internal SyntaxToken(SyntaxNode parent, GreenToken green, int position)
    {
        Parent = parent;
        Green = green;
        Position = position;
    }

    /// <summary>Gets the node the token belongs to.</summary>
    public SyntaxNode Parent { get; }

    /// <summary>Gets the offset in the file's text where the token starts, trivia included.</summary>
    public int Position { get; }

    /// <summary>Gets the kind of the token.</summary>
    public SyntaxKind Kind => Green.Kind;

    /// <summary>Gets the token's text, exactly as in the source.</summary>
    public string Text => Green.Text;

    /// <summary>
    /// Gets the instruction a <see cref="SyntaxKind.Mnemonic"/> token names, in any letter case,
    /// or <see cref="Syntax.MnemonicKind.None"/> for every other token.
    /// </summary>
    public MnemonicKind MnemonicKind => Green.MnemonicKind;

    /// <summary>
    /// Gets the directive a <see cref="SyntaxKind.Directive"/> token names, in any letter case, or
    /// <see cref="Syntax.DirectiveKind.None"/> for every other token, including a directive token
    /// that names a built-in function or no directive at all.
    /// </summary>
    public DirectiveKind DirectiveKind => Green.DirectiveKind;

    /// <summary>
    /// Gets a value indicating whether the token fills a place the syntax requires but the source
    /// leaves empty. Such a token's text is empty, and its span is the empty span where the token
    /// would have been.
    /// </summary>
    public bool IsMissing => Green.IsMissing;

    /// <summary>
    /// Gets a value indicating whether the token has a diagnostic. The diagnostic is a lexical
    /// error over the token's text or, on a missing token, a report of what the line needed in its
    /// place.
    /// </summary>
    public bool ContainsDiagnostics => Green.ContainsDiagnostics;

    /// <summary>Gets a value indicating whether the token has a <see cref="SyntaxAnnotation"/>.</summary>
    public bool ContainsAnnotations => Green.ContainsAnnotations;

    /// <summary>Gets the token's range, without trivia.</summary>
    public TextSpan Span => new(Position + Green.LeadingWidth, Green.Text.Length);

    /// <summary>Gets the token's range, including its trivia.</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>Gets the whitespace before the token. Only the first token on a line has any.</summary>
    public SyntaxTriviaList LeadingTrivia => new(this, Green.LeadingTrivia, Position);

    /// <summary>Gets the whitespace and comment after the token, up to the end of its line.</summary>
    public SyntaxTriviaList TrailingTrivia => new(this, Green.TrailingTrivia, Span.End);

    /// <summary>Gets the green token this token wraps.</summary>
    internal GreenToken Green { get; }

    /// <summary>Returns the token's text with its trivia, exactly as in the source.</summary>
    public string ToFullString() => Green.ToFullString();

    /// <summary>
    /// Returns the token that follows this token in the file. That is the next token of this line,
    /// or the first token of the line below, or null at the end of the file. The walk visits the
    /// same tokens as <see cref="SyntaxNode.DescendantTokens"/>, missing tokens included, and each
    /// returned token has the node it belongs to as its parent.
    /// </summary>
    public SyntaxToken? GetNextToken() => Step(1);

    /// <summary>
    /// Returns the token that precedes this token in the file. That is the token before it on
    /// this line, or the last token of the line above, or null at the start of the file.
    /// </summary>
    public SyntaxToken? GetPreviousToken() => Step(-1);

    /// <summary>
    /// Returns a copy of this token with <paramref name="text"/> as its text, keeping its kind and
    /// the trivia around it. If the text is unchanged, this token itself is returned. A rename
    /// uses this method, since the result is the same token of the same line with different text.
    /// </summary>
    /// <param name="text">The token's new text.</param>
    /// <returns>The token, which belongs to no file until a rewrite puts it into one.</returns>
    public SyntaxToken WithText(string text) =>
        text == Text ? this : Rebuilt(text, Green.LeadingTrivia, Green.TrailingTrivia);

    /// <summary>Returns a copy of this token with <paramref name="trivia"/> as its leading trivia.</summary>
    /// <param name="trivia">The whitespace to place before the token.</param>
    /// <returns>The token, which belongs to no file until a rewrite puts it into one.</returns>
    public SyntaxToken WithLeadingTrivia(params IEnumerable<SyntaxTrivia> trivia) =>
        Rebuilt(Text, Trivia(trivia), Green.TrailingTrivia);

    /// <summary>Returns a copy of this token with <paramref name="trivia"/> as its trailing trivia.</summary>
    /// <param name="trivia">The whitespace and comments to place after the token.</param>
    /// <returns>The token, which belongs to no file until a rewrite puts it into one.</returns>
    public SyntaxToken WithTrailingTrivia(params IEnumerable<SyntaxTrivia> trivia) =>
        Rebuilt(Text, Green.LeadingTrivia, Trivia(trivia));

    /// <summary>
    /// Returns a copy of this token that keeps its own kind and text but takes the leading and
    /// trailing trivia of <paramref name="other"/>. A fix that swaps one token for another uses
    /// this method so that the line's indentation and comment stay where they were.
    /// </summary>
    /// <param name="other">The token being replaced.</param>
    /// <returns>The token, which belongs to no file until a rewrite puts it into one.</returns>
    public SyntaxToken WithTriviaFrom(SyntaxToken other) =>
        Rebuilt(Text, other.Green.LeadingTrivia, other.Green.TrailingTrivia);

    /// <summary>
    /// Returns a copy of this token that has <paramref name="annotations"/> in addition to the
    /// annotations it already has. The copy is a <em>different</em> token from this one and
    /// belongs to no file until a rewrite puts it into one, so a rewrite can find it again
    /// afterwards.
    /// </summary>
    /// <param name="annotations">The annotations to add, skipping any the token already has.</param>
    /// <returns>This token if nothing was added; otherwise, the annotated copy.</returns>
    public SyntaxToken WithAdditionalAnnotations(params IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Green.Annotations;
        var wanted = own.AddRange(annotations.Where(annotation => !own.Contains(annotation)).Distinct());
        return wanted.Length == own.Length ? this : Annotated(wanted);
    }

    /// <summary>
    /// Returns a copy of this token without <paramref name="annotations"/>, keeping its other
    /// annotations.
    /// </summary>
    /// <param name="annotations">The annotations to remove.</param>
    /// <returns>This token if nothing was removed; otherwise, the copy.</returns>
    public SyntaxToken WithoutAnnotations(params IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Green.Annotations;
        var kept = own.RemoveRange(annotations);
        return kept.Length == own.Length ? this : Annotated(kept);
    }

    /// <summary>Returns a copy of this token without its annotations of <paramref name="kind"/>.</summary>
    /// <param name="kind">The kind of annotation to remove.</param>
    /// <returns>This token if nothing was removed; otherwise, the copy.</returns>
    public SyntaxToken WithoutAnnotations(string kind)
    {
        var own = Green.Annotations;
        var kept = own.RemoveAll(annotation => annotation.Kind == kind);
        return kept.Length == own.Length ? this : Annotated(kept);
    }

    /// <summary>Checks whether this token has <paramref name="annotation"/>.</summary>
    /// <param name="annotation">The annotation to look for, matched by reference.</param>
    public bool HasAnnotation(SyntaxAnnotation annotation) => Green.Annotations.Contains(annotation);

    /// <summary>Checks whether this token has an annotation of <paramref name="kind"/>.</summary>
    /// <param name="kind">The kind to look for.</param>
    public bool HasAnnotations(string kind) => GetAnnotations(kind).Any();

    /// <summary>
    /// Returns the annotations of <paramref name="kind"/> on this token, in the order they were
    /// added.
    /// </summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxAnnotation> GetAnnotations(string kind) =>
        Green.Annotations.Where(annotation => annotation.Kind == kind);

    /// <summary>Returns the syntax diagnostics on this token, in source order.</summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics()
    {
        var result = new List<Diagnostic>();
        Parent.Tree.Collect(Green, Position, result);
        return result;
    }

    /// <summary>Returns the token's text, without trivia.</summary>
    public override string ToString() => Text;

    /// <summary>Returns the green trivia of <paramref name="trivia"/>, in source order.</summary>
    private static ImmutableArray<GreenTrivia> Trivia(IEnumerable<SyntaxTrivia> trivia) =>
        [.. trivia.Select(one => one.Green)];

    /// <summary>
    /// Returns a detached copy of this token with <paramref name="wanted"/> in place of its
    /// annotations.
    /// </summary>
    private SyntaxToken Annotated(ImmutableArray<SyntaxAnnotation> wanted) =>
        SyntaxFactory.Detached((GreenToken)Green.WithAnnotations(wanted));

    /// <summary>
    /// Returns a copy of this token rebuilt with new text or trivia. A token's diagnostics concern
    /// the text it was lexed from, so a rebuilt token has none, because no source has been lexed
    /// with its text yet. Its annotations do not concern its text, so they are kept. The same
    /// token of the same line, with different text, is still the token that was tagged.
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
    /// Returns the token one step forwards or backwards from this token. The search works one line
    /// at a time, because a line has few enough tokens to walk them all. Stepping past either end
    /// of a line gives the first token of the next line or the last token of the previous line.
    /// </summary>
    /// <param name="direction">1 for the token after this token, −1 for the token before it.</param>
    private SyntaxToken? Step(int direction)
    {
        // A token inside a statement has one of the statement's nodes as its parent, so walk up
        // the parents to find the line that contains it.
        var owner = Parent;
        while (owner is not LineSyntax && owner.Parent is { } outer)
            owner = outer;

        // Two missing tokens can sit at the same position with nothing between them — `f(g(1`
        // is missing two `)` — so identifying a token takes its parent as well as its position,
        // which token equality does.
        var self = this;
        var (found, beside) = Beside(owner, direction, token => token == self);
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
    /// Finds the neighbour of the token that <paramref name="chosen"/> picks out among
    /// <paramref name="owner"/>'s tokens. The search makes one pass without building a list,
    /// because an editor asks for a token's neighbour very often.
    /// </summary>
    /// <param name="owner">The node whose tokens to walk, which is a line.</param>
    /// <param name="direction">1 for the token after the chosen token, −1 for the token before it.</param>
    /// <param name="chosen">The test that identifies the token whose neighbour to find.</param>
    /// <returns>
    /// Whether the chosen token was among the owner's tokens, and its neighbour. The neighbour is
    /// null when the chosen token is the first or the last of them.
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
