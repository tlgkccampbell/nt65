// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
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
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The routine's name.</summary>
    public SyntaxToken Name => ChildTokens[1];

    /// <summary>The <c>=</c>.</summary>
    public SyntaxToken EqualsToken => ChildTokens[2];

    /// <summary>Where the routine is.</summary>
    public ExpressionSyntax Address => FirstNode<ExpressionSyntax>()!;

    /// <summary>The signature, or null.</summary>
    public ProcSignatureSyntax? Signature => FirstNode<ProcSignatureSyntax>();

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitExternProcDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitExternProcDeclaration(this);
}
