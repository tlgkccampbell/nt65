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

    /// <summary>Tokens directly under this node, such as a line's.</summary>
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
                    var slot = Green.GetSlot(i);
                    if (slot is GreenToken token)
                        builder.Add(new SyntaxToken(this, token, position));
                    position += slot.FullWidth;
                }
                ImmutableInterlocked.InterlockedInitialize(ref childTokens, builder.ToImmutable());
            }
            return childTokens;
        }
    }

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

    /// <summary>The red node for each child that is not a token, in source order.</summary>
    private protected virtual ImmutableArray<SyntaxNode> CreateChildNodes()
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxNode>();
        var position = Position;
        for (var i = 0; i < Green.SlotCount; i++)
        {
            var slot = Green.GetSlot(i);
            if (slot is not GreenToken)
                builder.Add(slot.CreateRed(Tree, this, position));
            position += slot.FullWidth;
        }
        return builder.ToImmutable();
    }

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
        Locate(token) is var (slot, nodes, _) && slot + 1 < Green.SlotCount && Green.GetSlot(slot + 1) is not GreenToken
            ? ChildNodes[nodes]
            : null;

    /// <summary>The child written straight before <paramref name="token"/> when it is a node, or null.</summary>
    private protected SyntaxNode? NodeBefore(SyntaxToken? token) =>
        Locate(token) is var (slot, nodes, _) && slot > 0 && Green.GetSlot(slot - 1) is not GreenToken
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
            var text = position + token.LeadingWidth;
            if (start < 0)
                start = text;
            if (token.Kind != SyntaxKind.EndOfLine)
                end = text + token.Text.Length;
            return;
        }
        for (var i = 0; i < node.SlotCount; i++)
        {
            var slot = node.GetSlot(i);
            Measure(slot, position, ref start, ref end);
            position += slot.FullWidth;
        }
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
            var slot = Green.GetSlot(i);
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
