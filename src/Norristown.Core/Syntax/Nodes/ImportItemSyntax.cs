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
    public SyntaxToken? AddressSize => TokenAfter(ColonToken);

    /// <summary>The <c>proc(...)</c> after the <c>:</c>, or null.</summary>
    public ImportSignatureSyntax? Signature => FirstNode<ImportSignatureSyntax>();
}
