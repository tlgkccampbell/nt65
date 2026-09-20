// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
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
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>What is asserted.</summary>
    public ExpressionSyntax Condition =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>()! : SlotNode<ExpressionSyntax>(1);

    /// <summary>ca65's level, which nt65 reports, or null.</summary>
    public SyntaxToken? Level =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.Identifier) ?? FirstToken(SyntaxKind.Register) ?? FirstToken(SyntaxKind.Mnemonic) : SlotTokenOrNull(3);

    /// <summary>The quoted message, or null.</summary>
    public SyntaxToken? Message => Green is GreenSyntax ? FirstToken(SyntaxKind.StringLiteral) : SlotTokenOrNull(5);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitAssertDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitAssertDirective(this);
}
