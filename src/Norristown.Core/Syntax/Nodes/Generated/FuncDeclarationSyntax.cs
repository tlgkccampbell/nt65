// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
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
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The function's name, or null.</summary>
    public SyntaxToken? Name => Green is GreenSyntax ? NameAt(1) : SlotToken(1);

    /// <summary>The parameters, or null.</summary>
    public ParameterListSyntax? Parameters =>
        Green is GreenSyntax ? FirstNode<ParameterListSyntax>() : SlotNode<ParameterListSyntax>(2);

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Equals) : SlotToken(3);

    /// <summary>The expression the function stands for.</summary>
    public ExpressionSyntax Body =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>()! : SlotNode<ExpressionSyntax>(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitFuncDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitFuncDeclaration(this);
}
