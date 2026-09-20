// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.multiproc E, b: signature {</c>: one routine per member of the enum <c>E</c>.</summary>
public sealed class MultiProcDeclarationSyntax : StatementSyntax
{
    internal MultiProcDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.multiproc</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The enum whose members the routines are named from.</summary>
    public ExpressionSyntax Expression => FirstNode<ExpressionSyntax>()!;

    /// <summary>The <c>,</c> before the name, or null.</summary>
    public SyntaxToken? CommaToken => FirstToken(SyntaxKind.Comma);

    /// <summary>The name bound to the member, or null.</summary>
    public SyntaxToken? Name =>
        TokenAfter(CommaToken) is { Kind: SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic } name ? name : null;

    /// <summary>The signature, or null.</summary>
    public ProcSignatureSyntax? Signature => FirstNode<ProcSignatureSyntax>();

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitMultiProcDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitMultiProcDeclaration(this);
}
