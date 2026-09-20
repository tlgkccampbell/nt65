// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
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
    public SyntaxToken Name => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>=</c> before a checked value, or null.</summary>
    public SyntaxToken? EqualsToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Equals) : SlotTokenOrNull(1);

    /// <summary>The value the import is checked against, or null.</summary>
    public ExpressionSyntax? Value =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>() : SlotNodeOrNull<ExpressionSyntax>(2);

    /// <summary>The <c>:</c> before a size or a signature, or null.</summary>
    public SyntaxToken? ColonToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Colon) : SlotTokenOrNull(3);

    /// <summary>The <c>zp</c>, <c>abs</c> or <c>far</c> after the <c>:</c>, or null.</summary>
    public SyntaxToken? AddressSize =>
        Green is GreenSyntax ? TokenAfter(ColonToken) is { Kind: SyntaxKind.Identifier } size && SyntaxFacts.IsAddressSize(size.Text) ? size : null : SlotTokenOrNull(4);

    /// <summary>The <c>proc(...)</c> after the <c>:</c>, or null.</summary>
    public ImportSignatureSyntax? Signature =>
        Green is GreenSyntax ? FirstNode<ImportSignatureSyntax>() : SlotNodeOrNull<ImportSignatureSyntax>(5);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitImportItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitImportItem(this);
}
