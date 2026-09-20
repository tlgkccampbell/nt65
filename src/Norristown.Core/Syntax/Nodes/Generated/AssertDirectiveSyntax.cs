// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.assert expr, "message"</c>, whose message may be left out.</summary>
public sealed class AssertDirectiveSyntax : StatementSyntax
{
    internal AssertDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.assert</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>What is asserted.</summary>
    public ExpressionSyntax Condition => FirstNode<ExpressionSyntax>()!;

    /// <summary>ca65's level, which nt65 reports, or null.</summary>
    public SyntaxToken? Level =>
        FirstToken(SyntaxKind.Identifier) ?? FirstToken(SyntaxKind.Register) ?? FirstToken(SyntaxKind.Mnemonic);

    /// <summary>The quoted message, or null.</summary>
    public SyntaxToken? Message => FirstToken(SyntaxKind.StringLiteral);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitAssertDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitAssertDirective(this);
}
