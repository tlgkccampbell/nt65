using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// A red node: a green node with its parent and absolute position, created on demand.
/// Everything above the syntax layer works with these, through the class each kind of node
/// has: a <see cref="BinaryExpressionSyntax"/> has a left and a right, a
/// <see cref="ProcDeclarationSyntax"/> a name and a signature.
/// <para>
/// A node's shape is fixed: it has a slot for each piece the kind is written with, in source
/// order, and a required piece stands in its slot whether or not the source wrote it. So a
/// required property is never null, and a piece the source left out is a
/// <see cref="SyntaxToken"/> with <see cref="SyntaxToken.IsMissing"/> set, of no width, placed
/// where it belongs. A nullable property is null only when the piece belongs to an optional
/// part of the line that the source did not write at all.
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

    /// <summary>The tree this node belongs to.</summary>
    public SyntaxTree Tree { get; }

    /// <summary>The node containing this one, or null for the root.</summary>
    public SyntaxNode? Parent { get; }

    /// <summary>Where the node starts in the file's text, trivia included.</summary>
    public int Position { get; }

    /// <summary>What the node is.</summary>
    public SyntaxKind Kind => Green.Kind;

    /// <summary>The node's range in the file's text, trivia included.</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>
    /// The node's range without the trivia around it and without the line break that ends
    /// it: what an editor selects or reveals for the node. A node holding nothing but a line
    /// break is empty, positioned where the break is.
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

    /// <summary>The 0-based line this node starts on.</summary>
    public int LineIndex => Tree.GetLineIndex(Position);

    /// <summary>
    /// Whether this node or anything under it carries a diagnostic, answered without walking.
    /// </summary>
    public virtual bool ContainsDiagnostics => Green.ContainsDiagnostics;

    /// <summary>
    /// Whether this node or anything under it carries a <see cref="SyntaxAnnotation"/>, answered
    /// without walking, so that a search for annotated pieces walks only the subtrees that
    /// contain one.
    /// </summary>
    public virtual bool ContainsAnnotations => Green.ContainsAnnotations;

    /// <summary>Child lines and blocks of a file or block, a line's statement, or the nodes a statement is made of.</summary>
    public ImmutableArray<SyntaxNode> ChildNodes
    {
        get
        {
            if (childNodes.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref childNodes, CreateChildNodes());
            return childNodes;
        }
    }

    /// <summary>Tokens directly under this node, such as a line's, and the separators of its lists.</summary>
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

    /// <summary>The green node this one wraps.</summary>
    internal GreenNode Green { get; }

    /// <summary>
    /// The 0-based line this node ends on, which for a node written on one line is
    /// <see cref="LineIndex"/> and for a block or a file is the last line under it.
    /// </summary>
    internal int LastLineIndex => Tree.GetLineIndex(Math.Max(Position, FullSpan.End - 1));

    /// <summary>
    /// The node that this node's children report as their parent. It is this node, except for
    /// the internal node over a list: its items and separators report the node that holds the
    /// list, and the list node only caches their red nodes and is otherwise invisible.
    /// </summary>
    internal virtual SyntaxNode ChildParent => this;

    /// <summary>
    /// The children this node exposes instead of its slots, or null when its slots are its
    /// children. A line's slots are its tokens, but its children are the pieces it is made of,
    /// which hold those same tokens. Every walk of the tree goes through the pieces, so each
    /// token is reached once, with the node it is part of as its parent.
    /// </summary>
    internal virtual ImmutableArray<SyntaxNodeOrToken>? RedChildren => null;

    /// <summary>Calls the method <paramref name="visitor"/> has for this node's class.</summary>
    /// <param name="visitor">The visitor to call.</param>
    public abstract void Accept(SyntaxVisitor visitor);

    /// <summary>Calls the method <paramref name="visitor"/> has for this node's class.</summary>
    /// <typeparam name="TResult">The type of result the visitor computes.</typeparam>
    /// <param name="visitor">The visitor to call.</param>
    /// <returns>The result of that method.</returns>
    public abstract TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor);

    /// <summary>This node's children, nodes and tokens together, in source order.</summary>
    public ChildSyntaxList ChildNodesAndTokens() => new(this);

    /// <summary>
    /// The syntax diagnostics of this node and everything under it, in the order of the pieces
    /// that hold them. Nothing is walked where <see cref="ContainsDiagnostics"/> says there is
    /// nothing to find.
    /// <para>
    /// A line reports everything written on it, and a block or a file reports every line under
    /// it, errors about its braces included, so the root's result covers the whole file and is
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
    /// Every node below this one, parents before children. The walk keeps its own stack rather
    /// than recursing: a deeply nested expression can occur in real source, and a stack overflow
    /// would kill the process.
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

    /// <summary>The nearest node of type <typeparamref name="T"/> among this one and those containing it, or null.</summary>
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
    /// This node with <paramref name="oldNode"/> written as <paramref name="newNode"/>. Nothing
    /// else changes: every other character of the file stays the same, and a replacement that
    /// changes nothing returns this node itself.
    /// <para>
    /// A statement and everything under it is rebuilt in place. A line, a block and the file are
    /// written back out as text and parsed again, so replacing inside one of those returns the
    /// corresponding node of a <em>new</em> tree, which its <see cref="Tree"/> gives.
    /// </para>
    /// </summary>
    /// <param name="oldNode">The node to write over, which is under this one.</param>
    /// <param name="newNode">What to write there.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode ReplaceNode(SyntaxNode oldNode, SyntaxNode newNode) =>
        ReplaceNodes<SyntaxNode>([oldNode], (_, _) => newNode);

    /// <summary>
    /// This node with each of <paramref name="nodes"/> written as
    /// <paramref name="computeReplacement"/> says; a fix applied across a whole file uses this. A
    /// null replacement removes the node, which is allowed only for a list item or an optional
    /// piece.
    /// </summary>
    /// <typeparam name="TNode">What the nodes replaced are.</typeparam>
    /// <param name="nodes">The nodes to write over, which are under this one.</param>
    /// <param name="computeReplacement">
    /// Given the node as it was found, passed as both arguments, returns what to write there.
    /// </param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode ReplaceNodes<TNode>(
        IEnumerable<TNode> nodes, Func<TNode, TNode, SyntaxNode?> computeReplacement)
        where TNode : SyntaxNode =>
        new NodeReplacer<TNode>(nodes, computeReplacement).Visit(this)
            ?? throw new InvalidOperationException("a node cannot be taken out of itself");

    /// <summary>This node with <paramref name="oldToken"/> written as <paramref name="newToken"/>.</summary>
    /// <param name="oldToken">The token to write over, which is under this node.</param>
    /// <param name="newToken">What to write there.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode ReplaceToken(SyntaxToken oldToken, SyntaxToken newToken) =>
        ReplaceTokens([oldToken], (_, _) => newToken);

    /// <summary>This node with each of <paramref name="tokens"/> written as <paramref name="computeReplacement"/> says.</summary>
    /// <param name="tokens">The tokens to write over, which are under this node.</param>
    /// <param name="computeReplacement">
    /// Given the token as it was found, passed as both arguments, returns what to write there.
    /// </param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode ReplaceTokens(
        IEnumerable<SyntaxToken> tokens, Func<SyntaxToken, SyntaxToken, SyntaxToken> computeReplacement) =>
        new TokenReplacer(tokens, computeReplacement).Visit(this)
            ?? throw new InvalidOperationException("a node cannot be taken out of itself");

    /// <summary>
    /// This node without <paramref name="node"/>. A line goes with the break that ends it; an
    /// item of a list goes with the separator written after it; a piece that must be there cannot
    /// go at all.
    /// </summary>
    /// <param name="node">The node to take out, which is under this one.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode RemoveNode(SyntaxNode node) => ReplaceNodes<SyntaxNode>([node], (_, _) => null);

    /// <summary>
    /// This node with all its trivia, comments included, thrown away and replaced by standard
    /// spacing: one space where two tokens would otherwise run together, and none elsewhere. A
    /// node built from bare tokens needs this before it goes into a file: the factory invents no
    /// whitespace, so without this a built <c>lda #0</c> is written <c>lda#0</c>.
    /// <para>
    /// The result is readable nt65, not the file's layout: <see cref="Formatter"/> sets a line's
    /// indentation, and running it over the tree a rewrite returns is what moves a built line
    /// to its proper indentation.
    /// </para>
    /// </summary>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode NormalizeWhitespace()
    {
        var written = new List<(SyntaxToken Token, bool Tight)>();
        Flatten(this, written);
        var spaced = ImmutableArray.CreateBuilder<SyntaxToken>(written.Count);
        for (var i = 0; i < written.Count; i++)
        {
            var (token, tight) = written[i];
            SyntaxTrivia[] after = !tight && i + 1 < written.Count && Apart(token.Kind, written[i + 1].Token.Kind)
                ? [SyntaxFactory.Space]
                : [];
            spaced.Add(token.WithLeadingTrivia().WithTrailingTrivia(after));
        }
        return new Spacer(spaced.ToImmutable()).Visit(this) ?? this;
    }

    /// <summary>
    /// This node carrying <paramref name="annotations"/> as well as the ones it has. What comes
    /// back is a node of this node's own class, belonging to no file until a rewrite puts it into
    /// one, and it is a <em>different</em> node from this one: that is what lets a rewrite find it
    /// again afterwards.
    /// </summary>
    /// <param name="annotations">The annotations to put on, which it does not already carry.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode WithAdditionalAnnotations(params IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Green.Annotations;
        var wanted = own.AddRange(annotations.Where(annotation => !own.Contains(annotation)).Distinct());
        return wanted.Length == own.Length ? this : SyntaxTree.Detached(Green.WithAnnotations(wanted));
    }

    /// <summary>This node without <paramref name="annotations"/>, and with the rest of its own.</summary>
    /// <param name="annotations">The annotations to take off.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode WithoutAnnotations(params IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Green.Annotations;
        var kept = own.RemoveRange(annotations);
        return kept.Length == own.Length ? this : SyntaxTree.Detached(Green.WithAnnotations(kept));
    }

    /// <summary>This node without the annotations of <paramref name="kind"/> it carries.</summary>
    /// <param name="kind">The kind of annotation to take off.</param>
    /// <returns>This node, or the node it has become.</returns>
    public SyntaxNode WithoutAnnotations(string kind)
    {
        var own = Green.Annotations;
        var kept = own.RemoveAll(annotation => annotation.Kind == kind);
        return kept.Length == own.Length ? this : SyntaxTree.Detached(Green.WithAnnotations(kept));
    }

    /// <summary>Whether this node itself carries <paramref name="annotation"/>.</summary>
    /// <param name="annotation">The annotation to look for, matched by reference.</param>
    public bool HasAnnotation(SyntaxAnnotation annotation) => Green.Annotations.Contains(annotation);

    /// <summary>Whether this node itself carries an annotation of <paramref name="kind"/>.</summary>
    /// <param name="kind">The kind to look for.</param>
    public bool HasAnnotations(string kind) => GetAnnotations(kind).Any();

    /// <summary>The annotations of <paramref name="kind"/> on this node itself, in the order they were put on.</summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxAnnotation> GetAnnotations(string kind) =>
        Green.Annotations.Where(annotation => annotation.Kind == kind);

    /// <summary>
    /// Every node at or below this one carrying <paramref name="annotation"/>, in source order.
    /// It is how a fix finds the piece it tagged in the tree the rewrite gave back.
    /// </summary>
    /// <param name="annotation">The annotation to look for.</param>
    public IEnumerable<SyntaxNode> GetAnnotatedNodes(SyntaxAnnotation annotation) =>
        GetAnnotatedNodesAndTokens(annotation).Select(child => child.AsNode()).OfType<SyntaxNode>();

    /// <summary>Every node at or below this one carrying an annotation of <paramref name="kind"/>, in source order.</summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxNode> GetAnnotatedNodes(string kind) =>
        GetAnnotatedNodesAndTokens(kind).Select(child => child.AsNode()).OfType<SyntaxNode>();

    /// <summary>Every token below this node carrying <paramref name="annotation"/>, in source order.</summary>
    /// <param name="annotation">The annotation to look for.</param>
    public IEnumerable<SyntaxToken> GetAnnotatedTokens(SyntaxAnnotation annotation) =>
        GetAnnotatedNodesAndTokens(annotation).Where(child => child.IsToken).Select(child => child.AsToken());

    /// <summary>Every token below this node carrying an annotation of <paramref name="kind"/>, in source order.</summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxToken> GetAnnotatedTokens(string kind) =>
        GetAnnotatedNodesAndTokens(kind).Where(child => child.IsToken).Select(child => child.AsToken());

    /// <summary>Everything at or below this node carrying <paramref name="annotation"/>, in source order.</summary>
    /// <param name="annotation">The annotation to look for.</param>
    public IEnumerable<SyntaxNodeOrToken> GetAnnotatedNodesAndTokens(SyntaxAnnotation annotation) =>
        AnnotatedPieces(carried => carried.Contains(annotation));

    /// <summary>Everything at or below this node carrying an annotation of <paramref name="kind"/>, in source order.</summary>
    /// <param name="kind">The kind to look for.</param>
    public IEnumerable<SyntaxNodeOrToken> GetAnnotatedNodesAndTokens(string kind) =>
        AnnotatedPieces(carried => carried.Any(annotation => annotation.Kind == kind));

    /// <summary>The node's text, exactly as in the source.</summary>
    public string ToFullString() => Green.ToFullString();

    /// <summary>The node's text over <see cref="Span"/>: no surrounding trivia, no line break.</summary>
    public string GetText() => Tree.Text.Substring(Span.Start, Span.Length);

    /// <summary>The node's kind and range, for debugging.</summary>
    public override string ToString() => $"{Kind} at {FullSpan}";

    /// <summary>
    /// Everything below this node, nodes and tokens together, a parent before its children and
    /// siblings in source order.
    /// </summary>
    public IEnumerable<SyntaxNodeOrToken> DescendantNodesAndTokens()
    {
        foreach (var child in Below(this))
            yield return child;
    }

    /// <summary>
    /// Every token below this node, in source order, each of them once. A missing token is one
    /// of them: it stands in the tree where the piece it names belongs, with no text.
    /// </summary>
    public IEnumerable<SyntaxToken> DescendantTokens()
    {
        foreach (var child in Below(this))
        {
            if (child.AsNode() is null)
                yield return child.AsToken();
        }
    }

    /// <summary>The nodes containing this one, innermost first.</summary>
    public IEnumerable<SyntaxNode> Ancestors()
    {
        for (var node = Parent; node is not null; node = node.Parent)
            yield return node;
    }

    /// <summary>This node and the nodes containing it, this one first.</summary>
    public IEnumerable<SyntaxNode> AncestorsAndSelf()
    {
        for (SyntaxNode? node = this; node is not null; node = node.Parent)
            yield return node;
    }

    /// <summary>
    /// The first token of this node, or null when it has none. <paramref name="includeZeroWidth"/>
    /// takes in the tokens that write nothing: a missing token, and the line break of a file that
    /// ends without one.
    /// </summary>
    /// <param name="includeZeroWidth">Whether a token with no text counts.</param>
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
    /// The last token of this node, or null when it has none.
    /// <paramref name="includeZeroWidth"/> takes in the tokens that write nothing.
    /// </summary>
    /// <param name="includeZeroWidth">Whether a token with no text counts.</param>
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
    /// The token whose full span, trivia included, contains <paramref name="position"/>. The
    /// whitespace and the comment after a token belong to it, and the indentation before the
    /// first token of a line belongs to that token, so a caret in either finds the token the
    /// trivia is written beside, as in Roslyn. A missing token has no width and so contains no
    /// position, so it is never the result except at the end of a node, below.
    /// <para>
    /// The end of a node is past everything written in it and so falls in no token; the result
    /// there is the node's last token: the line break at the end of a file, or, in a node whose
    /// last piece the source leaves out, the missing token in its place.
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
    /// The whitespace or comment <paramref name="position"/> is written in, or null when the
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
    /// The innermost node containing the whole of <paramref name="span"/>, which is what an
    /// editor is asking about when it names a range. When no child of this node contains the
    /// whole span the result is this node, and a span outside this node is an error. An empty
    /// span is a caret rather than a selection: it belongs to what is written after it, not to
    /// what ends where it stands.
    /// </summary>
    /// <param name="span">A range within this node.</param>
    public SyntaxNode FindNode(TextSpan span)
    {
        if (!FullSpan.Contains(span))
            throw new ArgumentOutOfRangeException(nameof(span), span, "the span is not inside the node");
        return ChildHolding(span) ?? this;
    }

    /// <summary>
    /// <paramref name="built"/> with this node's own annotations added, which is what an
    /// <c>Update</c> returns: a node rebuilt from new pieces is still the node that was tagged,
    /// and the pieces keep whatever annotations they carry themselves.
    /// </summary>
    /// <typeparam name="T">What the node is.</typeparam>
    /// <param name="built">The node just rebuilt out of this one's pieces.</param>
    /// <returns>That node, or the node it has become.</returns>
    private protected T Annotated<T>(T built) where T : SyntaxNode =>
        Green.Annotations.IsEmpty ? built : (T)built.WithAdditionalAnnotations(Green.Annotations);

    /// <summary>
    /// Everything at or below this node carrying an annotation of any kind, in source order,
    /// which is what a rewrite reads to carry them across a reparse.
    /// </summary>
    internal IEnumerable<SyntaxNodeOrToken> AnnotatedPieces() => AnnotatedPieces(carried => !carried.IsEmpty);

    /// <summary>
    /// Everything at or below this node whose annotations <paramref name="wanted"/> accepts, in
    /// source order. Only the subtrees that say they hold an annotation are walked at all.
    /// </summary>
    /// <param name="wanted">Whether a piece's annotations are the ones being looked for.</param>
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

    /// <summary>Where slot <paramref name="index"/> starts in the file's text, trivia included.</summary>
    internal int SlotPosition(int index)
    {
        var position = Position;
        for (var i = 0; i < index; i++)
            position += Green.GetSlot(i)?.FullWidth ?? 0;
        return position;
    }

    /// <summary>
    /// The red node for slot <paramref name="index"/>, made on first use and kept, or null for
    /// a slot that holds a token or nothing.
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

    /// <summary>Whether <paramref name="node"/> is a list node, whose items its parent exposes directly.</summary>
    internal static bool IsList(GreenNode node) => node is GreenList or GreenSeparatedList;

    /// <summary>
    /// Adds this node's diagnostics to <paramref name="result"/>. For most nodes, what the green
    /// node and its children carry is the whole answer. A line, a block and a file differ: they
    /// hold a line's tokens rather than what the line parses to, so each overrides this to
    /// collect the tree's per-line diagnostics for the lines it spans.
    /// </summary>
    /// <param name="result">The list to add to.</param>
    private protected virtual void CollectDiagnostics(List<Diagnostic> result) =>
        Tree.Collect(Green, Position, result);

    /// <summary>
    /// The red node for each child that is not a token, in source order. A slot holding a list
    /// shows its items here rather than itself, as Roslyn's does, so nothing above the syntax
    /// layer ever meets the node over a list.
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

    /// <summary>The token in slot <paramref name="index"/>, which a required slot always holds.</summary>
    internal SyntaxToken SlotToken(int index) =>
        new(ChildParent, (GreenToken)Green.GetSlot(index)!, SlotPosition(index));

    /// <summary>The token in slot <paramref name="index"/>, or null when the slot is empty.</summary>
    private protected SyntaxToken? SlotTokenOrNull(int index) =>
        Green.GetSlot(index) is GreenToken token ? new SyntaxToken(ChildParent, token, SlotPosition(index)) : null;

    /// <summary>The node in slot <paramref name="index"/>, which a required slot always holds.</summary>
    private protected T SlotNode<T>(int index) where T : SyntaxNode => (T)SlotRed(index)!;

    /// <summary>The node in slot <paramref name="index"/>, or null when the slot is empty.</summary>
    private protected T? SlotNodeOrNull<T>(int index) where T : SyntaxNode => SlotRed(index) as T;

    /// <summary>The items of the list in slot <paramref name="index"/>; an empty slot is an empty list.</summary>
    private protected SyntaxList<T> SlotList<T>(int index) where T : SyntaxNode => new(SlotRed(index));

    /// <summary>The items and separators of the list in slot <paramref name="index"/>.</summary>
    private protected SeparatedSyntaxList<T> SlotSeparatedList<T>(int index) where T : SyntaxNode =>
        new(SlotRed(index));

    /// <summary>The tokens of the list in slot <paramref name="index"/>.</summary>
    private protected SyntaxTokenList SlotTokenList(int index) => new(SlotRed(index));

    /// <summary>
    /// Everything below <paramref name="node"/>, nodes and tokens together, a parent before its
    /// children and siblings in source order. The children being walked at each level are held
    /// here rather than in stack frames, so the stack used does not grow with how deeply the
    /// file nests.
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

    /// <summary>The first token's text start and the last non-line-break token's text end.</summary>
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
    /// Every token under <paramref name="node"/>, in source order, each paired with whether its
    /// parent writes it with no space before the next token: nt65 leaves no space inside a prefix
    /// operator and its operand, inside an instruction operand, or inside an address prefix.
    /// </summary>
    private static void Flatten(SyntaxNode node, List<(SyntaxToken Token, bool Tight)> written)
    {
        var tight = node is UnaryExpressionSyntax or OperandSyntax or AddressPrefixSyntax;
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.AsNode() is { } inner)
                Flatten(inner, written);
            else
                written.Add((child.AsToken(), tight));
        }
    }

    /// <summary>
    /// Whether a space goes between a token of <paramref name="left"/> and one of
    /// <paramref name="right"/>. Punctuation that binds tightly gets no space on its binding
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

    /// <summary>The binary operators, which are written clear of what they are between.</summary>
    private static bool Operator(SyntaxKind kind) => kind is SyntaxKind.Star or SyntaxKind.Slash
        or SyntaxKind.Plus or SyntaxKind.Minus or SyntaxKind.LessLess or SyntaxKind.GreaterGreater
        or SyntaxKind.Less or SyntaxKind.LessEquals or SyntaxKind.Greater or SyntaxKind.GreaterEquals
        or SyntaxKind.EqualsEquals or SyntaxKind.BangEquals or SyntaxKind.Ampersand
        or SyntaxKind.AmpersandAmpersand or SyntaxKind.Bar or SyntaxKind.BarBar or SyntaxKind.Caret
        or SyntaxKind.CaretCaret or SyntaxKind.Equals or SyntaxKind.Arrow;

    /// <summary>Word-like tokens, which need a space between them to be read as separate tokens.</summary>
    private static bool Word(SyntaxKind kind) => kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
        or SyntaxKind.Mnemonic or SyntaxKind.Register or SyntaxKind.Directive or SyntaxKind.NumberLiteral
        or SyntaxKind.CharacterLiteral or SyntaxKind.StringLiteral or SyntaxKind.CpuName or SyntaxKind.BadToken;

    /// <summary>
    /// The child whose full span contains <paramref name="position"/>, for the walk down to a
    /// token to continue through; default when no child does. A child of no width contains no
    /// position, so a missing token is never returned.
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
    /// The innermost node under this one holding the whole of <paramref name="span"/>, or null
    /// when no child contains it. A non-empty span is contained by one child at most, but an empty
    /// one is contained both by the child that ends where it stands and by the child that starts
    /// there, so every child containing it is searched and the narrowest result wins, with ties
    /// going to the later child.
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

    /// <summary>A rewrite that replaces the nodes it was given, or removes them from the tree.</summary>
    /// <typeparam name="TNode">What the nodes replaced are.</typeparam>
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
        /// A node that is being replaced is not walked into: its replacement is computed from
        /// the original node, which is what a fix expects to read.
        /// </remarks>
        public override SyntaxNode? Visit(SyntaxNode? node) =>
            node is TNode found && sought.Contains(node) ? replacement(found, found) : base.Visit(node);
    }

    /// <summary>A rewrite that replaces the tokens it was given.</summary>
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
    /// A rewrite that returns the already-spaced tokens it was given, in the order it visits
    /// them. The spacing was computed over the same walk order, so the nth token visited is
    /// the nth token in the list.
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
