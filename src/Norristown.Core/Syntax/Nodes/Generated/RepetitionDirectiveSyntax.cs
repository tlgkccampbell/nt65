// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>The line that opens a repetition: <c>.repeat count, name {</c> or <c>.each what, name {</c>.</summary>
public abstract class RepetitionDirectiveSyntax : StatementSyntax
{
    private protected RepetitionDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.repeat</c> or <c>.each</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The count of a <c>.repeat</c>, or what an <c>.each</c> goes through.</summary>
    public ExpressionSyntax Expression => FirstNode<ExpressionSyntax>()!;

    /// <summary>The <c>,</c> before the name, or null.</summary>
    public SyntaxToken? CommaToken => FirstToken(SyntaxKind.Comma);

    /// <summary>The name bound to the index or the item, or null when it is left out.</summary>
    public SyntaxToken? Name =>
        TokenAfter(CommaToken) is { Kind: SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic } name ? name : null;

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);
}
