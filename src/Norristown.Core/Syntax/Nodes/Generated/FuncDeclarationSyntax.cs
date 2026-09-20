// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.func name(a, b) = expr</c>: a pure expression function.</summary>
public sealed class FuncDeclarationSyntax : StatementSyntax
{
    internal FuncDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.func</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The function's name, or null.</summary>
    public SyntaxToken? Name => NameAt(1);

    /// <summary>The parameters, or null.</summary>
    public ParameterListSyntax? Parameters => FirstNode<ParameterListSyntax>();

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => FirstToken(SyntaxKind.Equals);

    /// <summary>The expression the function stands for.</summary>
    public ExpressionSyntax Body => FirstNode<ExpressionSyntax>()!;

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitFuncDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitFuncDeclaration(this);
}
