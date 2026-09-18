using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A line that starts with <c>.segment</c>: a declaration, a block's opener or a region line.</summary>
public abstract class SegmentStatementSyntax : StatementSyntax
{
    private protected SegmentStatementSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.segment</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The segment's name, or null. A quoted name is an error, and is still the name.</summary>
    public SyntaxToken? Name =>
        TokenAt(1) is { Kind: SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic or SyntaxKind.StringLiteral } name ? name : null;
}
