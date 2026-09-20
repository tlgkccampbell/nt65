// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name</c>, <c>name: size</c>, <c>name: proc(...)</c> or a checked <c>name = expr</c>.</summary>
public sealed class ImportItemSyntax : SyntaxNode
{
    internal ImportItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The name imported.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>=</c> before a checked value, or null.</summary>
    public SyntaxToken? EqualsToken => FirstToken(SyntaxKind.Equals);

    /// <summary>The value the import is checked against, or null.</summary>
    public ExpressionSyntax? Value => FirstNode<ExpressionSyntax>();

    /// <summary>The <c>:</c> before a size or a signature, or null.</summary>
    public SyntaxToken? ColonToken => FirstToken(SyntaxKind.Colon);

    /// <summary>The <c>zp</c>, <c>abs</c> or <c>far</c> after the <c>:</c>, or null.</summary>
    public SyntaxToken? AddressSize =>
        TokenAfter(ColonToken) is { Kind: SyntaxKind.Identifier } size && SyntaxFacts.IsAddressSize(size.Text) ? size : null;

    /// <summary>The <c>proc(...)</c> after the <c>:</c>, or null.</summary>
    public ImportSignatureSyntax? Signature => FirstNode<ImportSignatureSyntax>();

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitImportItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitImportItem(this);
}
