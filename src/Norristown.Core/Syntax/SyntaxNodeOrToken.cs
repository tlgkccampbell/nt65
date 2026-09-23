namespace Norristown.Syntax;

/// <summary>
/// One child of a node: either a node or a token. It is what a node's children read as when
/// both kinds are wanted together, as in <see cref="SyntaxNode.ChildNodesAndTokens"/> and a
/// separated list's items and separators.
/// <para>
/// A default value is neither a node nor a token: its kind is <see cref="SyntaxKind.None"/>
/// and it has no text and no place in the file.
/// </para>
/// </summary>
public readonly struct SyntaxNodeOrToken
{
    private readonly SyntaxNode? node;
    private readonly SyntaxToken token;

    internal SyntaxNodeOrToken(SyntaxNode node)
    {
        this.node = node;
        token = default;
    }

    internal SyntaxNodeOrToken(SyntaxToken token)
    {
        node = null;
        this.token = token;
    }

    /// <summary>Whether this is a node.</summary>
    public bool IsNode => node is not null;

    /// <summary>Whether this is a token.</summary>
    public bool IsToken => node is null && token.Green is not null;

    /// <summary>What the node or token is, or <see cref="SyntaxKind.None"/> for neither.</summary>
    public SyntaxKind Kind => node is not null ? node.Kind : IsToken ? token.Kind : SyntaxKind.None;

    /// <summary>The node this one is a child of, or null.</summary>
    public SyntaxNode? Parent => node is not null ? node.Parent : IsToken ? token.Parent : null;

    /// <summary>Where it starts in the file's text, trivia included.</summary>
    public int Position => node is not null ? node.Position : IsToken ? token.Position : 0;

    /// <summary>Its range in the file's text, trivia included.</summary>
    public TextSpan FullSpan => node is not null ? node.FullSpan : IsToken ? token.FullSpan : default;

    /// <summary>Its range without the trivia around it.</summary>
    public TextSpan Span => node is not null ? node.Span : IsToken ? token.Span : default;

    /// <summary>The node as a child.</summary>
    /// <param name="node">The node.</param>
    public static implicit operator SyntaxNodeOrToken(SyntaxNode node) => new(node);

    /// <summary>The token as a child.</summary>
    /// <param name="token">The token.</param>
    public static implicit operator SyntaxNodeOrToken(SyntaxToken token) => new(token);

    /// <summary>The node, or null when this is a token.</summary>
    public SyntaxNode? AsNode() => node;

    /// <summary>The token, or a default token when this is a node.</summary>
    public SyntaxToken AsToken() => token;

    /// <summary>Its text, exactly as in the source.</summary>
    public string ToFullString() => node is not null ? node.ToFullString() : IsToken ? token.ToFullString() : "";

    /// <summary>
    /// Its text over <see cref="Span"/>: no surrounding trivia and no line break. This reads a
    /// list's items and separators alike without checking which of the two each one is.
    /// </summary>
    public string GetText() => node is not null ? node.GetText() : IsToken ? token.Text : "";

    /// <summary>Its kind and range, for debugging.</summary>
    public override string ToString() => node is not null ? node.ToString() : IsToken ? token.ToString() : "";
}
