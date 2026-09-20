// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.proc name = address: entry -&gt; exit</c>: a routine with no body.</summary>
public sealed class ExternProcDeclarationSyntax : StatementSyntax
{
    internal ExternProcDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.proc</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The routine's name.</summary>
    public SyntaxToken Name => Green is GreenSyntax ? ChildTokens[1] : SlotToken(1);

    /// <summary>The <c>=</c>.</summary>
    public SyntaxToken EqualsToken => Green is GreenSyntax ? ChildTokens[2] : SlotToken(2);

    /// <summary>Where the routine is.</summary>
    public ExpressionSyntax Address =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>()! : SlotNode<ExpressionSyntax>(3);

    /// <summary>The signature, or null.</summary>
    public ProcSignatureSyntax? Signature =>
        Green is GreenSyntax ? FirstNode<ProcSignatureSyntax>() : SlotNodeOrNull<ProcSignatureSyntax>(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitExternProcDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitExternProcDeclaration(this);
}
