using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name</c> or <c>outer::inner</c>, then <c>: size</c> or <c>as "linker_name"</c>.</summary>
public sealed class ExportItemSyntax : SyntaxNode
{
    internal ExportItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The name exported.</summary>
    public NameExpressionSyntax Name => (NameExpressionSyntax)ChildNodes[0];

    /// <summary>The <c>:</c> before the address size, or null.</summary>
    public SyntaxToken? ColonToken => FirstToken(SyntaxKind.Colon);

    /// <summary>The <c>zp</c>, <c>abs</c> or <c>far</c> after the <c>:</c>, or null.</summary>
    public SyntaxToken? AddressSize =>
        TokenAfter(ColonToken) is { Kind: SyntaxKind.Identifier } size && SyntaxFacts.IsAddressSize(size.Text) ? size : null;

    /// <summary>The <c>as</c> before the linker name, or null.</summary>
    public SyntaxToken? AsKeyword => FirstWord("as");

    /// <summary>The quoted name the linker sees, or null.</summary>
    public SyntaxToken? LinkerName => FirstToken(SyntaxKind.StringLiteral);
}
