// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.next @a, gfx::init</c>, or <c>.next ?</c>.</summary>
public sealed class NextDirectiveSyntax : StatementSyntax
{
    private ImmutableArray<NameExpressionSyntax> targets;

    internal NextDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.next</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>?</c> of <c>.next ?</c>, or null.</summary>
    public SyntaxToken? QuestionToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Question) : SlotTokenOrNull(1);

    /// <summary>The labels flow continues at.</summary>
    public ImmutableArray<NameExpressionSyntax> Targets => Nodes(ref targets);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitNextDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitNextDirective(this);
}
