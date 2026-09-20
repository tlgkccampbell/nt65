// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.error "message"</c> or <c>.warning "message"</c>.</summary>
public sealed class ErrorDirectiveSyntax : StatementSyntax
{
    internal ErrorDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.error</c> or <c>.warning</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The quoted message, or null.</summary>
    public SyntaxToken? Message => Green is GreenSyntax ? FirstToken(SyntaxKind.StringLiteral) : SlotToken(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitErrorDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitErrorDirective(this);
}
