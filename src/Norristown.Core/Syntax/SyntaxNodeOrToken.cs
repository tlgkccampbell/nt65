namespace Norristown.Syntax;

/// <summary>
/// Represents one child of a node, which is either a node or a token. A node's children take this
/// form when both kinds are wanted together, as in <see cref="SyntaxNode.ChildNodesAndTokens"/>
/// and in a separated list's items and separators.
/// <para>
/// A default value is neither a node nor a token. Its kind is <see cref="SyntaxKind.None"/>, and
/// it has no text and no position in the file.
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

    /// <summary>Gets a value indicating whether this is a node.</summary>
    public bool IsNode => node is not null;

    /// <summary>Gets a value indicating whether this is a token.</summary>
    public bool IsToken => node is null && token.Green is not null;

    /// <summary>
    /// Gets the kind of the node or token, or <see cref="SyntaxKind.None"/> if this is neither.
    /// </summary>
    public SyntaxKind Kind => node is not null ? node.Kind : IsToken ? token.Kind : SyntaxKind.None;

    /// <summary>Gets the node that this node or token is a child of, or null if there is none.</summary>
    public SyntaxNode? Parent => node is not null ? node.Parent : IsToken ? token.Parent : null;

    /// <summary>Gets the offset in the file's text where the node or token starts, including trivia.</summary>
    public int Position => node is not null ? node.Position : IsToken ? token.Position : 0;

    /// <summary>Gets the range of the node or token in the file's text, including trivia.</summary>
    public TextSpan FullSpan => node is not null ? node.FullSpan : IsToken ? token.FullSpan : default;

    /// <summary>Gets the range of the node or token without the trivia around it.</summary>
    public TextSpan Span => node is not null ? node.Span : IsToken ? token.Span : default;

    /// <summary>Converts a node to a child value.</summary>
    /// <param name="node">The node.</param>
    public static implicit operator SyntaxNodeOrToken(SyntaxNode node) => new(node);

    /// <summary>Converts a token to a child value.</summary>
    /// <param name="token">The token.</param>
    public static implicit operator SyntaxNodeOrToken(SyntaxToken token) => new(token);

    /// <summary>Returns the node, or null if this is a token.</summary>
    public SyntaxNode? AsNode() => node;

    /// <summary>Returns the token, or a default token if this is a node.</summary>
    public SyntaxToken AsToken() => token;

    /// <summary>Returns the full text of the node or token, exactly as in the source.</summary>
    public string ToFullString() => node is not null ? node.ToFullString() : IsToken ? token.ToFullString() : "";

    /// <summary>
    /// Returns the text over <see cref="Span"/>, without surrounding trivia or the line break.
    /// This method reads a list's items and separators alike, without checking whether each child
    /// is an item or a separator.
    /// </summary>
    public string GetText() => node is not null ? node.GetText() : IsToken ? token.Text : "";

    /// <summary>Returns the kind and range of the node or token, for debugging.</summary>
    public override string ToString() => node is not null ? node.ToString() : IsToken ? token.ToString() : "";
}
