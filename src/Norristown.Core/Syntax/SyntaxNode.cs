using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// A red node: a green node with its parent and absolute position, created on demand.
/// Everything above the syntax layer works with these, through the class each kind of node
/// has: a <see cref="BinaryExpressionSyntax"/> has a left and a right, a
/// <see cref="ProcDeclarationSyntax"/> a name and a signature.
/// <para>
/// The parser never invents a token, so a piece it expected and did not find is absent from
/// the node, and the property that names it is null. A property is non-null only where no
/// line can parse to the node without the piece.
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

    /// <summary>The green node this one wraps.</summary>
    public GreenNode Green { get; }

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

    /// <summary>
    /// The node a child of this one hangs from. It is this node, except on the internal node
    /// over a list, whose items and separators belong to the node that holds the list: the list
    /// node keeps their red nodes and is otherwise invisible.
    /// </summary>
    internal virtual SyntaxNode ChildParent => this;

    /// <summary>
    /// The children this node shows in place of its slots, or null where its slots are its
    /// children. A line's slots are its tokens, and its children are the pieces it is written
    /// in, which hold those same tokens: everything that walks the tree reads the pieces, so
    /// that a token belongs to the node it is part of and is met once.
    /// </summary>
    internal virtual ImmutableArray<SyntaxNodeOrToken>? RedChildren => null;

    /// <summary>Hands this node to the method <paramref name="visitor"/> has for its class.</summary>
    /// <param name="visitor">The visitor to hand it to.</param>
    public abstract void Accept(SyntaxVisitor visitor);

    /// <summary>Hands this node to the method <paramref name="visitor"/> has for its class.</summary>
    /// <typeparam name="TResult">What visiting a node works out.</typeparam>
    /// <param name="visitor">The visitor to hand it to.</param>
    /// <returns>What that method worked out.</returns>
    public abstract TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor);

    /// <summary>This node's children, nodes and tokens together, in source order.</summary>
    public ChildSyntaxList ChildNodesAndTokens() => new(this);

    /// <summary>Every node below this one, parents before children.</summary>
    public IEnumerable<SyntaxNode> DescendantNodes()
    {
        foreach (var child in ChildNodes)
        {
            yield return child;
            foreach (var descendant in child.DescendantNodes())
                yield return descendant;
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
        foreach (var child in ChildNodesAndTokens())
        {
            yield return child;
            if (child.AsNode() is { } node)
            {
                foreach (var descendant in node.DescendantNodesAndTokens())
                    yield return descendant;
            }
        }
    }

    /// <summary>
    /// Every token below this node, in source order, each of them once. A missing token is one
    /// of them: it stands in the tree where the piece it names belongs, with no text.
    /// </summary>
    public IEnumerable<SyntaxToken> DescendantTokens()
    {
        foreach (var child in ChildNodesAndTokens())
        {
            if (child.AsNode() is { } node)
            {
                foreach (var token in node.DescendantTokens())
                    yield return token;
            }
            else
            {
                yield return child.AsToken();
            }
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
    /// The token <paramref name="position"/> is written in, trivia and all: the token whose full
    /// span holds the position. The whitespace and the comment after a token belong to it, and
    /// the indentation before the first token of a line belongs to that token, so a caret in
    /// either finds the token the trivia is written beside, as it does in Roslyn. A missing token
    /// has no width and so holds no position and is never the answer. The end of the file, and
    /// the end of any node, is past everything written in it and gives its last token.
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
    /// The innermost node holding the whole of <paramref name="span"/>, which is what an editor
    /// is asking about when it names a range. A span no child of this node holds whole gives this
    /// node itself, and a span this node does not hold at all is an error. An empty span is a
    /// caret rather than a selection: it belongs to what is written after it, not to what ends
    /// where it stands.
    /// </summary>
    /// <param name="span">A range within this node.</param>
    public SyntaxNode FindNode(TextSpan span)
    {
        if (!FullSpan.Contains(span))
            throw new ArgumentOutOfRangeException(nameof(span), span, "the span is not inside the node");
        return ChildHolding(span) ?? this;
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

    /// <summary>Whether <paramref name="node"/> is the node over a list, which a parent shows through.</summary>
    internal static bool IsList(GreenNode node) => node is GreenList or GreenSeparatedList;

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
    private protected SyntaxToken SlotToken(int index) =>
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

    /// <summary>The token at <paramref name="index"/> among <see cref="ChildTokens"/>, or null when there are not that many.</summary>
    private protected SyntaxToken? TokenAt(int index) => index < ChildTokens.Length ? ChildTokens[index] : null;

    /// <summary>The token at <paramref name="index"/> when it is one a name may be written as, or null.</summary>
    private protected SyntaxToken? NameAt(int index) =>
        TokenAt(index) is { Kind: SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic } name ? name : null;

    /// <summary>The first token of <paramref name="kind"/> directly under this node, or null.</summary>
    private protected SyntaxToken? FirstToken(SyntaxKind kind)
    {
        foreach (var token in ChildTokens)
        {
            if (token.Kind == kind)
                return token;
        }
        return null;
    }

    /// <summary>The first contextual word <paramref name="word"/> directly under this node, such as <c>as</c>, or null.</summary>
    private protected SyntaxToken? FirstWord(string word)
    {
        foreach (var token in ChildTokens)
        {
            if (token.Kind == SyntaxKind.Identifier && token.Text.Equals(word, StringComparison.OrdinalIgnoreCase))
                return token;
        }
        return null;
    }

    /// <summary>The first child node of type <typeparamref name="T"/>, or null.</summary>
    private protected T? FirstNode<T>() where T : SyntaxNode
    {
        foreach (var node in ChildNodes)
        {
            if (node is T found)
                return found;
        }
        return null;
    }

    /// <summary>Every child node of type <typeparamref name="T"/>, made once and kept in <paramref name="cache"/>.</summary>
    private protected ImmutableArray<T> Nodes<T>(ref ImmutableArray<T> cache) where T : SyntaxNode
    {
        if (cache.IsDefault)
            ImmutableInterlocked.InterlockedInitialize(ref cache, [.. ChildNodes.OfType<T>()]);
        return cache;
    }

    /// <summary>The child written straight after <paramref name="token"/> when it is a node, or null.</summary>
    private protected SyntaxNode? NodeAfter(SyntaxToken? token) =>
        Locate(token) is var (slot, nodes, _) && slot + 1 < Green.SlotCount
        && Green.GetSlot(slot + 1) is not (null or GreenToken)
            ? ChildNodes[nodes]
            : null;

    /// <summary>The child written straight before <paramref name="token"/> when it is a node, or null.</summary>
    private protected SyntaxNode? NodeBefore(SyntaxToken? token) =>
        Locate(token) is var (slot, nodes, _) && slot > 0 && Green.GetSlot(slot - 1) is not (null or GreenToken)
            ? ChildNodes[nodes - 1]
            : null;

    /// <summary>The child written straight after <paramref name="token"/> when it is a token, or null.</summary>
    private protected SyntaxToken? TokenAfter(SyntaxToken? token) =>
        Locate(token) is var (slot, _, tokens) && slot + 1 < Green.SlotCount && Green.GetSlot(slot + 1) is GreenToken
            ? ChildTokens[tokens + 1]
            : null;

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
    /// The child whose text holds <paramref name="position"/>, which the walk down to a token
    /// goes on through; nothing when no child does. A child of no width holds no position, so a
    /// missing token is never it.
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
    /// when no child of it does. A span with a length is held by one child at most, and an empty
    /// one is held both by what ends where it stands and by what starts there, so every child
    /// that could hold it is followed down and the narrowest answer, latest written, is the one.
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

    /// <summary>
    /// Where a token of this node sits among its children: its slot, and how many nodes and
    /// how many tokens come before it. Null for no token, or one that is not this node's own.
    /// </summary>
    private (int Slot, int Nodes, int Tokens)? Locate(SyntaxToken? token)
    {
        if (token is not { } sought || !ReferenceEquals(sought.Parent, this))
            return null;
        int nodes = 0, tokens = 0, position = Position;
        for (var i = 0; i < Green.SlotCount; i++)
        {
            if (Green.GetSlot(i) is not { } slot)
                continue;
            if (slot is GreenToken)
            {
                if (position == sought.Position && ReferenceEquals(slot, sought.Green))
                    return (i, nodes, tokens);
                tokens++;
            }
            else
            {
                nodes++;
            }
            position += slot.FullWidth;
        }
        return null;
    }
}
