// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.signature std = a8, i16, dp = 0</c>: a name for items a signature uses.</summary>
public sealed class SignatureDeclarationSyntax : StatementSyntax
{
    internal SignatureDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.signature</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The set's name, or null.</summary>
    public SyntaxToken? Name => Green is GreenSyntax ? NameAt(1) : SlotToken(1);

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Equals) : SlotToken(2);

    /// <summary>The items the name stands for, or null.</summary>
    public StateListSyntax? Items => Green is GreenSyntax ? FirstNode<StateListSyntax>() : SlotNode<StateListSyntax>(3);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitSignatureDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitSignatureDeclaration(this);
}
