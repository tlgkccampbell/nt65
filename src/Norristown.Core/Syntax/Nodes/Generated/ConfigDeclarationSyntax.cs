// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.config NAME = value</c>: a setting, whose value the build may give instead.</summary>
public sealed class ConfigDeclarationSyntax : StatementSyntax
{
    internal ConfigDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.config</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The setting's name, or null.</summary>
    public SyntaxToken? Name =>
        Green is GreenSyntax ? TokenAt(1) is { Kind: SyntaxKind.Identifier } name ? name : null : SlotToken(1);

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Equals) : SlotToken(2);

    /// <summary>The setting's value, or null.</summary>
    public ExpressionSyntax? Value =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>() : SlotNode<ExpressionSyntax>(3);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitConfigDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitConfigDeclaration(this);
}
