// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.union name {</c>.</summary>
public sealed class UnionDeclarationSyntax : TypeDeclarationSyntax
{
    internal UnionDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <inheritdoc/>
    public override SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <inheritdoc/>
    public override SyntaxToken? Name => Green is GreenSyntax ? NameAt(1) : SlotTokenOrNull(1);

    /// <inheritdoc/>
    public override SyntaxToken? OpenBraceToken =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitUnionDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitUnionDeclaration(this);
}
