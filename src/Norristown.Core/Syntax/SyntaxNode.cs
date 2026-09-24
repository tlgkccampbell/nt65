using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents a node of a syntax tree. This is the red node, which wraps a green node, adds its
/// parent and absolute position, and is created on demand. Everything above the syntax layer
/// works with red nodes through the class for each kind of node. For example, a
/// <see cref="BinaryExpressionSyntax"/> has a left and a right operand, and a
/// <see cref="ProcDeclarationSyntax"/> has a name and a signature.
/// <para>
/// A <em>piece</em> of a node is one of the tokens, nodes or lists that its kind is made of,
/// such as the operator of a binary expression. A <em>slot</em> is a numbered position in the
/// green node's fixed layout. Each piece has its own slot, and the slots are in source order.
/// </para>
/// <para>
/// A required piece occupies its slot whether or not the source contains it, so a required
/// property is never null. A piece the source leaves out is a <see cref="SyntaxToken"/> with
/// <see cref="SyntaxToken.IsMissing"/> set, of zero width, at the position where it belongs. A
/// nullable property is null only when the piece belongs to an optional part of the line that
/// the source omits entirely.
/// </para>
/// </summary>
public abstract class SyntaxNode
{
    private ImmutableArray<SyntaxNode> childNodes;
    private ImmutableArray<SyntaxToken> childTokens;
    private SyntaxNode?[]? slots;

    private protected SyntaxNode(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
    {
        Tree = tree;
        Parent = parent;
        Green = green;
        Position = position;
    }

    /// <summary>Gets the tree this node belongs to.</summary>
    public SyntaxTree Tree { get; }

    /// <summary>Gets the node that contains this node, or null if this node is the root.</summary>
    public SyntaxNode? Parent { get; }

    /// <summary>Gets the offset in the file's text where this node starts, including trivia.</summary>
    public int Position { get; }

    /// <summary>Gets the kind of this node.</summary>
    public SyntaxKind Kind => Green.Kind;

    /// <summary>Gets the node's range in the file's text, including trivia.</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>
    /// Gets the node's range without the trivia around it and without the line break that ends
    /// it. This is the range an editor selects or reveals for the node. For a node that contains
    /// only a line break, the range is empty and positioned where the break is.
    /// </summary>
    public TextSpan Span
    {
        get
        {
            int start = -1, end = -1;
            Measure(Green, Position, ref start, ref end);
            return start < 0 ? new TextSpan(Position, 0) : new TextSpan(start, end > start ? end - start : 0);
        }
    }

    /// <summary>Gets the 0-based index of the line this node starts on.</summary>
    public int LineIndex => Tree.GetLineIndex(Position);

    /// <summary>
    /// Gets a value indicating whether this node or anything under it has a diagnostic. The
    /// answer is computed without walking the tree.
    /// </summary>
    public virtual bool ContainsDiagnostics => Green.ContainsDiagnostics;

    /// <summary>
    /// Gets a value indicating whether this node or anything under it has a
    /// <see cref="SyntaxAnnotation"/>. The answer is computed without walking the tree, so a search
    /// for annotated pieces walks only the subtrees that contain an annotation.
    /// </summary>
    public virtual bool ContainsAnnotations => Green.ContainsAnnotations;

    /// <summary>
    /// Gets the child nodes of this node. These are the lines and blocks of a file or block, the
    /// statement of a line, or the nodes a statement is made of.
    /// </summary>
    public ImmutableArray<SyntaxNode> ChildNodes
    {
        get
        {
            if (childNodes.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref childNodes, CreateChildNodes());
            return childNodes;
        }
    }

    /// <summary>
    /// Gets the tokens directly under this node, such as a line's tokens, and the separators of
    /// its lists.
    /// </summary>
    public ImmutableArray<SyntaxToken> ChildTokens
    {
        get
        {
            if (childTokens.IsDefault)
            {
                var builder = ImmutableArray.CreateBuilder<SyntaxToken>();
                var position = Position;
                for (var i = 0; i < Green.SlotCount; i++)
                {
                    if (Green.GetSlot(i) is not { } slot)
                        continue;
                    if (slot is GreenToken token)
                        builder.Add(new SyntaxToken(ChildParent, token, position));
                    else if (IsList(slot))
                        builder.AddRange(SlotRed(i)!.ChildTokens);
                    position += slot.FullWidth;
                }
                ImmutableInterlocked.InterlockedInitialize(ref childTokens, builder.ToImmutable());
            }
            return childTokens;
        }
    }

    /// <summary>Gets the green node that this node wraps.</summary>
    internal GreenNode Green { get; }

    /// <summary>
    /// Gets the 0-based index of the line this node ends on. For a node on a single line this is
    /// <see cref="LineIndex"/>, and for a block or a file it is the last line under it.
    /// </summary>
    internal int LastLineIndex => Tree.GetLineIndex(Math.Max(Position, FullSpan.End - 1));

    /// <summary>
    /// Gets the node that this node's children report as their parent. This is the node itself,
    /// except for the internal node over a list. The items and separators of a list report the
    /// node that holds the list, and the list node only caches their red nodes and is otherwise
    /// invisible.
    /// </summary>
    internal virtual SyntaxNode ChildParent => this;

    /// <summary>
    /// Gets the children this node exposes instead of its slots, or null if its slots are its
    /// children. A line's slots are its tokens, but its children are the pieces it is made of,
    /// which contain those same tokens. Every walk of the tree goes through the pieces, so each
    /// token is reached once, with the node it is part of as its parent.
    /// </summary>
    internal virtual ImmutableArray<SyntaxNodeOrToken>? RedChildren => null;

    /// <summary>Calls the method of <paramref name="visitor"/> that handles this node's class.</summary>
    /// <param name="visitor">The visitor to call.</param>
    public abstract void Accept(SyntaxVisitor visitor);

    /// <summary>
    /// Calls the method of <paramref name="visitor"/> that handles this node's class and returns
    /// its result.
    /// </summary>
    /// <typeparam name="TResult">The type of result the visitor computes.</typeparam>
    /// <param name="visitor">The visitor to call.</param>
    /// <returns>The result of that method.</returns>
    public abstract TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor);

    /// <summary>Returns this node's child nodes and tokens together, in source order.</summary>
    public ChildSyntaxList ChildNodesAndTokens() => new(this);

    /// <summary>
    /// Returns the syntax diagnostics of this node and everything under it, in the order of the
    /// pieces that hold them. A subtree is not walked when <see cref="ContainsDiagnostics"/> is
    /// false for it.
    /// <para>
    /// A line reports every diagnostic on it. A block or a file reports those of every line under
    /// it, including errors about its braces, so the root's result covers the whole file and is
    /// the same list as <see cref="SyntaxTree.Diagnostics"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics()
    {
        var result = new List<Diagnostic>();
        CollectDiagnostics(result);
        return result;
    }

    /// <summary>
    /// Returns every node below this node, with parents before children. The walk keeps its own
    /// stack rather than recursing, because a deeply nested expression can occur in real source,
    /// and a stack overflow would kill the process.
    /// </summary>
    public IEnumerable<SyntaxNode> DescendantNodes()
    {
        var pending = new Stack<SyntaxNode>();
        PushChildren(pending, this);
        while (pending.TryPop(out var node))
        {
            yield return node;
            PushChildren(pending, node);
        }
    }

    /// <summary>
    /// Returns the nearest node of type <typeparamref name="T"/> among this node and the nodes
    /// that contain it, or null if there is none.
    /// </summary>
    public T? FirstAncestorOrSelf<T>() where T : SyntaxNode
    {
        for (var node = this; node is not null; node = node.Parent)
        {
            if (node is T found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Returns a copy of this node in which <paramref name="oldNode"/> is replaced with
    /// <paramref name="newNode"/>. The rest of the file's text is unchanged. If the replacement
    /// changes nothing, this node itself is returned.
    /// <para>
    /// A statement and everything under it is rebuilt in place. A line, a block and the file are
    /// converted back to text and parsed again, so replacing inside one of those returns the
    /// corresponding node of a <em>new</em> tree, which the result's <see cref="Tree"/> gives.
    /// </para>
    /// </summary>
    /// <param name="oldNode">The node to replace, which is under this node.</param>
    /// <param name="newNode">The node to put in its place.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode ReplaceNode(SyntaxNode oldNode, SyntaxNode newNode) =>
        ReplaceNodes<SyntaxNode>([oldNode], (_, _) => newNode);

    /// <summary>
    /// Returns a copy of this node in which each of <paramref name="nodes"/> is replaced with the
    /// node that <paramref name="computeReplacement"/> returns for it. A fix applied across a whole
    /// file uses this method. A null replacement removes the node, which is allowed only for a
    /// list item or an optional piece.
    /// </summary>
    /// <typeparam name="TNode">The type of the nodes to replace.</typeparam>
    /// <param name="nodes">The nodes to replace, which are under this node.</param>
    /// <param name="computeReplacement">
    /// A function that receives the node as it was found, passed as both arguments, and returns
    /// the node to put in its place.
    /// </param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode ReplaceNodes<TNode>(
        IEnumerable<TNode> nodes, Func<TNode, TNode, SyntaxNode?> computeReplacement)
        where TNode : SyntaxNode =>
        new NodeReplacer<TNode>(nodes, computeReplacement).Visit(this)
            ?? throw new InvalidOperationException("a node cannot be taken out of itself");

    /// <summary>
    /// Returns a copy of this node in which <paramref name="oldToken"/> is replaced with
    /// <paramref name="newToken"/>.
    /// </summary>
    /// <param name="oldToken">The token to replace, which is under this node.</param>
    /// <param name="newToken">The token to put in its place.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode ReplaceToken(SyntaxToken oldToken, SyntaxToken newToken) =>
        ReplaceTokens([oldToken], (_, _) => newToken);

    /// <summary>
    /// Returns a copy of this node in which each of <paramref name="tokens"/> is replaced with the
    /// token that <paramref name="computeReplacement"/> returns for it.
    /// </summary>
    /// <param name="tokens">The tokens to replace, which are under this node.</param>
    /// <param name="computeReplacement">
    /// A function that receives the token as it was found, passed as both arguments, and returns
    /// the token to put in its place.
    /// </param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode ReplaceTokens(
        IEnumerable<SyntaxToken> tokens, Func<SyntaxToken, SyntaxToken, SyntaxToken> computeReplacement) =>
        new TokenReplacer(tokens, computeReplacement).Visit(this)
            ?? throw new InvalidOperationException("a node cannot be taken out of itself");

    /// <summary>
    /// Returns a copy of this node from which <paramref name="node"/> is removed. A line is
    /// removed together with the line break that ends it, and a list item together with the
    /// separator after it. A required piece cannot be removed.
    /// </summary>
    /// <param name="node">The node to remove, which is under this node.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode RemoveNode(SyntaxNode node) => ReplaceNodes<SyntaxNode>([node], (_, _) => null);

    /// <summary>
    /// Returns a copy of this node in which all trivia, including comments, is discarded and
    /// replaced by standard spacing. Standard spacing puts one space where two tokens would
    /// otherwise run together, and none elsewhere. A node built from bare tokens needs this before
    /// it goes into a file, because the factory adds no whitespace, so a built <c>lda #0</c> would
    /// otherwise come out as <c>lda#0</c>.
    /// <para>
    /// The result is readable nt65, but it does not follow the file's layout.
    /// <see cref="Formatter"/> sets a line's indentation, so running it over the tree that a
    /// rewrite returns moves a built line to its proper indentation.
    /// </para>
    /// </summary>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode NormalizeWhitespace()
    {
        var tokens = new List<(SyntaxToken Token, bool Tight)>();
        Flatten(this, tokens);
        var spaced = ImmutableArray.CreateBuilder<SyntaxToken>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var (token, tight) = tokens[i];
            SyntaxTrivia[] after = !tight && i + 1 < tokens.Count && Apart(token.Kind, tokens[i + 1].Token.Kind)
                ? [SyntaxFactory.Space]
                : [];
            spaced.Add(token.WithLeadingTrivia().WithTrailingTrivia(after));
        }
        return new Spacer(spaced.ToImmutable()).Visit(this) ?? this;
    }

    /// <summary>
    /// Returns a copy of this node that has <paramref name="annotations"/> in addition to the
    /// annotations it already has. The result is a node of this node's own class. It belongs to no
    /// file until a rewrite puts it into one, and it is a <em>different</em> node from this one,
    /// so that a rewrite can find it again afterwards.
    /// </summary>
    /// <param name="annotations">
    /// The annotations to add. Annotations this node already has are skipped.
    /// </param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode WithAdditionalAnnotations(params IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Green.Annotations;
        var wanted = own.AddRange(annotations.Where(annotation => !own.Contains(annotation)).Distinct());
        return wanted.Length == own.Length ? this : SyntaxTree.Detached(Green.WithAnnotations(wanted));
    }

    /// <summary>
    /// Returns a copy of this node without <paramref name="annotations"/>, keeping its other
    /// annotations.
    /// </summary>
    /// <param name="annotations">The annotations to remove.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode WithoutAnnotations(params IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Green.Annotations;
        var kept = own.RemoveRange(annotations);
        return kept.Length == own.Length ? this : SyntaxTree.Detached(Green.WithAnnotations(kept));
    }

    /// <summary>
    /// Returns a copy of this node without its annotations of kind <paramref name="kind"/>.
    /// </summary>
    /// <param name="kind">The kind of annotation to remove.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode WithoutAnnotations(string kind)
    {
        var own = Green.Annotations;
        var kept = own.RemoveAll(annotation => annotation.Kind == kind);
        return kept.Length == own.Length ? this : SyntaxTree.Detached(Green.WithAnnotations(kept));
    }

    /// <summary>Checks whether this node itself has <paramref name="annotation"/>.</summary>
    /// <param name="annotation">The annotation to look for, matched by reference.</param>
    public bool HasAnnotation(SyntaxAnnotation annotation) => Green.Annotations.Contains(annotation);

    /// <summary>Checks whether this node itself has an annotation of kind <paramref name="kind"/>.</summary>
    /// <param name="kind">The kind to look for.</param>
    public bool HasAnnotations(string kind) => GetAnnotations(kind).Any();

    /// <summary>
    /// Returns the annotations of kind <paramref name="kind"/> on this node itself, in the order
    /// they were added.
    /// </summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxAnnotation> GetAnnotations(string kind) =>
        Green.Annotations.Where(annotation => annotation.Kind == kind);

    /// <summary>
    /// Returns every node at or below this node that has <paramref name="annotation"/>, in source
    /// order. A fix uses this method to find the piece it tagged in the tree that the rewrite
    /// returned.
    /// </summary>
    /// <param name="annotation">The annotation to look for.</param>
    public IEnumerable<SyntaxNode> GetAnnotatedNodes(SyntaxAnnotation annotation) =>
        GetAnnotatedNodesAndTokens(annotation).Select(child => child.AsNode()).OfType<SyntaxNode>();

    /// <summary>
    /// Returns every node at or below this node that has an annotation of kind
    /// <paramref name="kind"/>, in source order.
    /// </summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxNode> GetAnnotatedNodes(string kind) =>
        GetAnnotatedNodesAndTokens(kind).Select(child => child.AsNode()).OfType<SyntaxNode>();

    /// <summary>
    /// Returns every token below this node that has <paramref name="annotation"/>, in source order.
    /// </summary>
    /// <param name="annotation">The annotation to look for.</param>
    public IEnumerable<SyntaxToken> GetAnnotatedTokens(SyntaxAnnotation annotation) =>
        GetAnnotatedNodesAndTokens(annotation).Where(child => child.IsToken).Select(child => child.AsToken());

    /// <summary>
    /// Returns every token below this node that has an annotation of kind <paramref name="kind"/>,
    /// in source order.
    /// </summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxToken> GetAnnotatedTokens(string kind) =>
        GetAnnotatedNodesAndTokens(kind).Where(child => child.IsToken).Select(child => child.AsToken());

    /// <summary>
    /// Returns every node and token at or below this node that has <paramref name="annotation"/>,
    /// in source order.
    /// </summary>
    /// <param name="annotation">The annotation to look for.</param>
    public IEnumerable<SyntaxNodeOrToken> GetAnnotatedNodesAndTokens(SyntaxAnnotation annotation) =>
        AnnotatedPieces(annotations => annotations.Contains(annotation));

    /// <summary>
    /// Returns every node and token at or below this node that has an annotation of kind
    /// <paramref name="kind"/>, in source order.
    /// </summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxNodeOrToken> GetAnnotatedNodesAndTokens(string kind) =>
        AnnotatedPieces(annotations => annotations.Any(annotation => annotation.Kind == kind));

    /// <summary>Returns the node's full text, including trivia, exactly as in the source.</summary>
    public string ToFullString() => Green.ToFullString();

    /// <summary>
    /// Returns the node's text over <see cref="Span"/>, without surrounding trivia or the line break.
    /// </summary>
    public string GetText() => Tree.Text.Substring(Span.Start, Span.Length);

    /// <summary>Returns the node's kind and range, for debugging.</summary>
    public override string ToString() => $"{Kind} at {FullSpan}";

    /// <summary>
    /// Returns every node and token below this node, with a parent before its children and
    /// siblings in source order.
    /// </summary>
    public IEnumerable<SyntaxNodeOrToken> DescendantNodesAndTokens()
    {
        foreach (var child in Below(this))
            yield return child;
    }

    /// <summary>
    /// Returns every token below this node once each, in source order. Missing tokens are
    /// included; a missing token has no text and stands where the piece it represents belongs.
    /// </summary>
    public IEnumerable<SyntaxToken> DescendantTokens()
    {
        foreach (var child in Below(this))
        {
            if (child.AsNode() is null)
                yield return child.AsToken();
        }
    }

    /// <summary>Returns the nodes that contain this node, innermost first.</summary>
    public IEnumerable<SyntaxNode> Ancestors()
    {
        for (var node = Parent; node is not null; node = node.Parent)
            yield return node;
    }

    /// <summary>Returns this node followed by the nodes that contain it, innermost first.</summary>
    public IEnumerable<SyntaxNode> AncestorsAndSelf()
    {
        for (SyntaxNode? node = this; node is not null; node = node.Parent)
            yield return node;
    }

    /// <summary>
    /// Returns the first token of this node, or null if it has none.
    /// <paramref name="includeZeroWidth"/> makes the search include tokens with no text, which
    /// are missing tokens and the line break of a file that ends without one.
    /// </summary>
    /// <param name="includeZeroWidth">A value indicating whether a token with no text counts.</param>
    public SyntaxToken? GetFirstToken(bool includeZeroWidth = false)
    {
        foreach (var token in DescendantTokens())
        {
            if (includeZeroWidth || token.Span.Length > 0)
                return token;
        }
        return null;
    }

    /// <summary>
    /// Returns the last token of this node, or null if it has none.
    /// <paramref name="includeZeroWidth"/> makes the search include tokens with no text.
    /// </summary>
    /// <param name="includeZeroWidth">A value indicating whether a token with no text counts.</param>
    public SyntaxToken? GetLastToken(bool includeZeroWidth = false)
    {
        SyntaxToken? found = null;
        foreach (var token in DescendantTokens())
        {
            if (includeZeroWidth || token.Span.Length > 0)
                found = token;
        }
        return found;
    }

    /// <summary>
    /// Returns the token whose full span, including trivia, contains <paramref name="position"/>.
    /// The whitespace and the comment after a token belong to it, and the indentation before the
    /// first token of a line belongs to that token. A caret in either trivia therefore finds the
    /// token next to it, as in Roslyn. A missing token has zero width and contains no position,
    /// so it is never the result except at the end of a node, described below.
    /// <para>
    /// The end of a node is past all of its text, so it falls in no token. At that position the
    /// result is the node's last token. This is the line break at the end of a file or, in a node
    /// whose last piece the source leaves out, the missing token in its place.
    /// </para>
    /// </summary>
    /// <param name="position">An offset from this node's start to its end.</param>
    public SyntaxToken FindToken(int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, Position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, FullSpan.End);
        if (position == FullSpan.End)
        {
            return GetLastToken(includeZeroWidth: true)
                ?? throw new ArgumentOutOfRangeException(nameof(position), position, "the node holds no token");
        }

        // A file is blocks of lines, so the line holding the position is found by binary search
        // and the walk down starts there, rather than stepping over every block before it.
        var node = Kind is SyntaxKind.File or SyntaxKind.Block ? Tree.GetLine(Tree.GetLineIndex(position)) : this;
        while (true)
        {
            var child = node.ChildContaining(position);
            if (child.AsNode() is not { } inner)
                return child.AsToken();
            node = inner;
        }
    }

    /// <summary>
    /// Returns the whitespace or comment that contains <paramref name="position"/>, or null if the
    /// position is in a token's own text.
    /// </summary>
    /// <param name="position">An offset from this node's start to its end.</param>
    public SyntaxTrivia? FindTrivia(int position)
    {
        var token = FindToken(position);
        foreach (var trivia in token.LeadingTrivia)
        {
            if (trivia.Span.Contains(position))
                return trivia;
        }
        foreach (var trivia in token.TrailingTrivia)
        {
            if (trivia.Span.Contains(position))
                return trivia;
        }
        return null;
    }

    /// <summary>
    /// Returns the innermost node that contains the whole of <paramref name="span"/>, which is the
    /// node an editor asks about when it names a range. If no child of this node contains the
    /// whole span, the result is this node. A span outside this node is an error. An empty span
    /// is a caret rather than a selection, so it belongs to the text that follows it, not to the
    /// text that ends at its position.
    /// </summary>
    /// <param name="span">A range within this node.</param>
    public SyntaxNode FindNode(TextSpan span)
    {
        if (!FullSpan.Contains(span))
            throw new ArgumentOutOfRangeException(nameof(span), span, "the span is not inside the node");
        return ChildHolding(span) ?? this;
    }

    /// <summary>
    /// Returns <paramref name="built"/> with this node's own annotations added. An
    /// <c>Update</c> method returns this, because a node rebuilt from new pieces is still the node
    /// that was tagged. The pieces keep their own annotations.
    /// </summary>
    /// <typeparam name="T">The type of the node.</typeparam>
    /// <param name="built">The node just rebuilt out of this one's pieces.</param>
    /// <returns>That node, or the node it has become.</returns>
    private protected T Annotated<T>(T built) where T : SyntaxNode =>
        Green.Annotations.IsEmpty ? built : (T)built.WithAdditionalAnnotations(Green.Annotations);

    /// <summary>
    /// Returns every node and token at or below this node that has an annotation of any kind, in
    /// source order. A rewrite reads these so that the annotations survive a reparse.
    /// </summary>
    internal IEnumerable<SyntaxNodeOrToken> AnnotatedPieces() => AnnotatedPieces(annotations => !annotations.IsEmpty);

    /// <summary>
    /// Returns every node and token at or below this node whose annotations
    /// <paramref name="wanted"/> accepts, in source order. Only the subtrees whose
    /// <see cref="ContainsAnnotations"/> is true are walked.
    /// </summary>
    /// <param name="wanted">
    /// A function that checks whether a piece's annotations are the ones being looked for.
    /// </param>
    private IEnumerable<SyntaxNodeOrToken> AnnotatedPieces(Func<ImmutableArray<SyntaxAnnotation>, bool> wanted)
    {
        if (!ContainsAnnotations)
            yield break;
        if (wanted(Green.Annotations))
            yield return this;
        foreach (var child in ChildNodesAndTokens())
        {
            if (child.AsNode() is { } inner)
            {
                foreach (var found in inner.AnnotatedPieces(wanted))
                    yield return found;
            }
            else if (child.AsToken() is { ContainsAnnotations: true } token && wanted(token.Green.Annotations))
            {
                yield return token;
            }
        }
    }

    /// <summary>
    /// Returns the offset in the file's text where slot <paramref name="index"/> starts, including
    /// trivia.
    /// </summary>
    internal int SlotPosition(int index)
    {
        var position = Position;
        for (var i = 0; i < index; i++)
            position += Green.GetSlot(i)?.FullWidth ?? 0;
        return position;
    }

    /// <summary>
    /// Returns the red node for slot <paramref name="index"/>, which is created on first use and
    /// cached. Returns null if the slot holds a token or is empty.
    /// </summary>
    internal SyntaxNode? SlotRed(int index)
    {
        if (Green.GetSlot(index) is not { } green || green is GreenToken)
            return null;
        var cache = slots;
        if (cache is null)
        {
            var made = new SyntaxNode?[Green.SlotCount];
            cache = Interlocked.CompareExchange(ref slots, made, null) ?? made;
        }
        if (cache[index] is { } red)
            return red;
        var created = green.CreateRed(Tree, ChildParent, SlotPosition(index));
        return Interlocked.CompareExchange(ref cache[index], created, null) ?? created;
    }

    /// <summary>
    /// Checks whether <paramref name="node"/> is a list node, whose items its parent exposes directly.
    /// </summary>
    internal static bool IsList(GreenNode node) => node is GreenList or GreenSeparatedList;

    /// <summary>
    /// Adds this node's diagnostics to <paramref name="result"/>. For most nodes, the diagnostics
    /// on the green node and its children are the complete set. A line, a block and a file hold a
    /// line's tokens rather than what the line parses to, so each of them overrides this method
    /// to collect the tree's per-line diagnostics for the lines it spans.
    /// </summary>
    /// <param name="result">The list to add to.</param>
    private protected virtual void CollectDiagnostics(List<Diagnostic> result) =>
        Tree.Collect(Green, Position, result);

    /// <summary>
    /// Creates the red node for each child that is not a token, in source order. A slot that
    /// holds a list contributes its items rather than the list node, as in Roslyn, so nothing
    /// above the syntax layer ever sees the node over a list.
    /// </summary>
    private protected virtual ImmutableArray<SyntaxNode> CreateChildNodes()
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxNode>();
        for (var i = 0; i < Green.SlotCount; i++)
        {
            if (Green.GetSlot(i) is not { } slot || slot is GreenToken)
                continue;
            if (IsList(slot))
                builder.AddRange(SlotRed(i)!.ChildNodes);
            else
                builder.Add(SlotRed(i)!);
        }
        return builder.ToImmutable();
    }

    /// <summary>Returns the token in slot <paramref name="index"/>, which a required slot always holds.</summary>
    internal SyntaxToken SlotToken(int index) =>
        new(ChildParent, (GreenToken)Green.GetSlot(index)!, SlotPosition(index));

    /// <summary>Returns the token in slot <paramref name="index"/>, or null if the slot is empty.</summary>
    private protected SyntaxToken? SlotTokenOrNull(int index) =>
        Green.GetSlot(index) is GreenToken token ? new SyntaxToken(ChildParent, token, SlotPosition(index)) : null;

    /// <summary>Returns the node in slot <paramref name="index"/>, which a required slot always holds.</summary>
    private protected T SlotNode<T>(int index) where T : SyntaxNode => (T)SlotRed(index)!;

    /// <summary>Returns the node in slot <paramref name="index"/>, or null if the slot is empty.</summary>
    private protected T? SlotNodeOrNull<T>(int index) where T : SyntaxNode => SlotRed(index) as T;

    /// <summary>
    /// Returns the items of the list in slot <paramref name="index"/>. An empty slot gives an
    /// empty list.
    /// </summary>
    private protected SyntaxList<T> SlotList<T>(int index) where T : SyntaxNode => new(SlotRed(index));

    /// <summary>Returns the items and separators of the list in slot <paramref name="index"/>.</summary>
    private protected SeparatedSyntaxList<T> SlotSeparatedList<T>(int index) where T : SyntaxNode =>
        new(SlotRed(index));

    /// <summary>Returns the tokens of the list in slot <paramref name="index"/>.</summary>
    private protected SyntaxTokenList SlotTokenList(int index) => new(SlotRed(index));

    /// <summary>
    /// Returns every node and token below <paramref name="node"/>, with a parent before its
    /// children and siblings in source order. The enumerators for each level are kept on an
    /// explicit stack rather than in call frames, so the call stack does not grow with the depth
    /// of nesting in the file.
    /// </summary>
    /// <param name="node">The node to walk below.</param>
    private static IEnumerable<SyntaxNodeOrToken> Below(SyntaxNode node)
    {
        var pending = new Stack<IEnumerator<SyntaxNodeOrToken>>();
        pending.Push(node.ChildNodesAndTokens().GetEnumerator());
        try
        {
            while (pending.Count > 0)
            {
                var children = pending.Peek();
                if (!children.MoveNext())
                {
                    pending.Pop().Dispose();
                    continue;
                }
                var child = children.Current;
                yield return child;
                if (child.AsNode() is { } inner)
                    pending.Push(inner.ChildNodesAndTokens().GetEnumerator());
            }
        }
        finally
        {
            // A caller that stops early leaves unfinished levels, whose enumerators are disposed here.
            foreach (var children in pending)
                children.Dispose();
        }
    }

    /// <summary>
    /// Puts <paramref name="node"/>'s children on <paramref name="pending"/>, last first, so
    /// that they come off it in source order.
    /// </summary>
    /// <param name="pending">The walk's stack.</param>
    /// <param name="node">The node whose children to push.</param>
    private static void PushChildren(Stack<SyntaxNode> pending, SyntaxNode node)
    {
        var children = node.ChildNodes;
        for (var i = children.Length - 1; i >= 0; i--)
            pending.Push(children[i]);
    }

    /// <summary>
    /// Finds the start of the first token's text and the end of the last token's text, ignoring
    /// line breaks for the end.
    /// </summary>
    private static void Measure(GreenNode node, int position, ref int start, ref int end)
    {
        if (node is GreenToken token)
        {
            // A missing token is nowhere in the text, so it neither starts nor ends the range.
            if (token.IsMissing)
                return;
            var text = position + token.LeadingWidth;
            if (start < 0)
                start = text;
            if (token.Kind != SyntaxKind.EndOfLine)
                end = text + token.Text.Length;
            return;
        }
        for (var i = 0; i < node.SlotCount; i++)
        {
            if (node.GetSlot(i) is not { } slot)
                continue;
            Measure(slot, position, ref start, ref end);
            position += slot.FullWidth;
        }
    }

    /// <summary>
    /// Adds every token under <paramref name="node"/> to <paramref name="tokens"/>, in source
    /// order. Each token is paired with a value indicating whether its parent allows no space
    /// before the next token. nt65 puts no space between a prefix operator and its operand, inside
    /// an instruction operand, or inside an address prefix.
    /// </summary>
    private static void Flatten(SyntaxNode node, List<(SyntaxToken Token, bool Tight)> tokens)
    {
        var tight = node is UnaryExpressionSyntax or OperandSyntax or AddressPrefixSyntax;
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.AsNode() is { } inner)
                Flatten(inner, tokens);
            else
                tokens.Add((child.AsToken(), tight));
        }
    }

    /// <summary>
    /// Checks whether a space goes between a token of kind <paramref name="left"/> and a token of
    /// kind <paramref name="right"/>. Punctuation that binds tightly gets no space on its binding
    /// side, two adjacent words need a space to stay separate, and a binary operator gets a
    /// space on both sides.
    /// </summary>
    private static bool Apart(SyntaxKind left, SyntaxKind right)
    {
        if (right is SyntaxKind.EndOfLine or SyntaxKind.Comma or SyntaxKind.CloseParen or SyntaxKind.CloseBracket
            or SyntaxKind.Colon or SyntaxKind.ColonColon or SyntaxKind.OpenBracket or SyntaxKind.DotDot)
        {
            return false;
        }
        if (left is SyntaxKind.ColonColon or SyntaxKind.Hash or SyntaxKind.OpenParen or SyntaxKind.OpenBracket
            or SyntaxKind.DotDot or SyntaxKind.Bang or SyntaxKind.Tilde)
        {
            return false;
        }
        if (left is SyntaxKind.Colon or SyntaxKind.Comma or SyntaxKind.Mnemonic or SyntaxKind.CloseBrace)
            return true;
        if (left is SyntaxKind.Directive)
            return right != SyntaxKind.OpenParen;
        if (left is SyntaxKind.OpenBrace || right is SyntaxKind.OpenBrace or SyntaxKind.CloseBrace)
            return true;
        return Operator(left) || Operator(right) || (Word(left) && Word(right));
    }

    /// <summary>
    /// Checks whether <paramref name="kind"/> is a binary operator, which gets a space on both
    /// sides.
    /// </summary>
    private static bool Operator(SyntaxKind kind) => kind is SyntaxKind.Star or SyntaxKind.Slash
        or SyntaxKind.Plus or SyntaxKind.Minus or SyntaxKind.LessLess or SyntaxKind.GreaterGreater
        or SyntaxKind.Less or SyntaxKind.LessEquals or SyntaxKind.Greater or SyntaxKind.GreaterEquals
        or SyntaxKind.EqualsEquals or SyntaxKind.BangEquals or SyntaxKind.Ampersand
        or SyntaxKind.AmpersandAmpersand or SyntaxKind.Bar or SyntaxKind.BarBar or SyntaxKind.Caret
        or SyntaxKind.CaretCaret or SyntaxKind.Equals or SyntaxKind.Arrow;

    /// <summary>
    /// Checks whether <paramref name="kind"/> is a word-like token, which needs a space between it
    /// and another word to be read as a separate token.
    /// </summary>
    private static bool Word(SyntaxKind kind) => kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
        or SyntaxKind.Mnemonic or SyntaxKind.Register or SyntaxKind.Directive or SyntaxKind.NumberLiteral
        or SyntaxKind.CharacterLiteral or SyntaxKind.StringLiteral or SyntaxKind.CpuName or SyntaxKind.BadToken;

    /// <summary>
    /// Returns the child whose full span contains <paramref name="position"/>, through which the
    /// walk down to a token continues, or the default value if no child contains it. A child of
    /// zero width contains no position, so a missing token is never returned.
    /// </summary>
    private SyntaxNodeOrToken ChildContaining(int position)
    {
        if (RedChildren is { } children)
        {
            foreach (var child in children)
            {
                if (child.FullSpan.Contains(position))
                    return child;
            }
            return default;
        }
        var at = Position;
        for (var i = 0; i < Green.SlotCount; i++)
        {
            if (Green.GetSlot(i) is not { } slot)
                continue;
            if (position < at + slot.FullWidth)
            {
                return slot is GreenToken token
                    ? new SyntaxNodeOrToken(new SyntaxToken(ChildParent, token, at))
                    : new SyntaxNodeOrToken(SlotRed(i)!);
            }
            at += slot.FullWidth;
        }
        return default;
    }

    /// <summary>
    /// Returns the innermost node under this node that contains the whole of
    /// <paramref name="span"/>, or null if no child contains it. At most one child contains a
    /// non-empty span. An empty span is contained both by the child that ends at its position and
    /// by the child that starts there. So every child that contains the span is searched, and the
    /// narrowest result wins, with ties going to the later child.
    /// </summary>
    private SyntaxNode? ChildHolding(TextSpan span)
    {
        SyntaxNode? best = null;
        foreach (var child in ChildNodes)
        {
            if (!child.FullSpan.Contains(span))
                continue;
            var found = child.ChildHolding(span) ?? child;
            if (best is null || found.FullSpan.Length <= best.FullSpan.Length)
                best = found;
        }
        return best;
    }

    /// <summary>Replaces the given nodes, or removes them from the tree, during a rewrite.</summary>
    /// <typeparam name="TNode">The type of the nodes to replace.</typeparam>
    private sealed class NodeReplacer<TNode> : SyntaxRewriter where TNode : SyntaxNode
    {
        private readonly HashSet<SyntaxNode> sought;
        private readonly Func<TNode, TNode, SyntaxNode?> replacement;

        internal NodeReplacer(IEnumerable<TNode> nodes, Func<TNode, TNode, SyntaxNode?> replacement)
        {
            sought = [.. nodes];
            this.replacement = replacement;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A node that is being replaced is not walked into. Its replacement is computed from
        /// the original node, because that is the node a fix expects to read.
        /// </remarks>
        public override SyntaxNode? Visit(SyntaxNode? node) =>
            node is TNode found && sought.Contains(node) ? replacement(found, found) : base.Visit(node);
    }

    /// <summary>Replaces the given tokens during a rewrite.</summary>
    private sealed class TokenReplacer : SyntaxRewriter
    {
        private readonly HashSet<SyntaxToken> sought;
        private readonly Func<SyntaxToken, SyntaxToken, SyntaxToken> replacement;

        internal TokenReplacer(IEnumerable<SyntaxToken> tokens, Func<SyntaxToken, SyntaxToken, SyntaxToken> replacement)
        {
            sought = [.. tokens];
            this.replacement = replacement;
        }

        /// <inheritdoc/>
        public override SyntaxToken VisitToken(SyntaxToken token) =>
            sought.Contains(token) ? replacement(token, token) : token;
    }

    /// <summary>
    /// Replaces each token with the corresponding already-spaced token it was given, in the order
    /// it visits them. The spacing was computed over the same walk order, so the nth token visited
    /// is the nth token in the list.
    /// </summary>
    /// <param name="spaced">Every token of the node, in source order, with its new trivia.</param>
    private sealed class Spacer(ImmutableArray<SyntaxToken> spaced) : SyntaxRewriter
    {
        private int index;

        /// <inheritdoc/>
        public override SyntaxToken VisitToken(SyntaxToken token) =>
            index < spaced.Length ? spaced[index++] : token;
    }
}
