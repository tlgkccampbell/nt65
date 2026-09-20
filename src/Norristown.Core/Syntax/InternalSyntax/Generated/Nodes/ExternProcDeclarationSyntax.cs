// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ExternProcDeclarationSyntax"/>.
/// <c>.proc name = address: entry -&gt; exit</c>: a routine with no body.
/// </summary>
internal sealed class ExternProcDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly GreenToken equalsToken;
    private readonly GreenNode address;
    private readonly GreenNode? signature;

    internal ExternProcDeclarationSyntax(
        GreenToken keyword,
        GreenToken name,
        GreenToken equalsToken,
        GreenNode address,
        GreenNode? signature)
        : base(SyntaxKind.ExternProcDeclaration, keyword.FullWidth + name.FullWidth + equalsToken.FullWidth + address.FullWidth + (signature?.FullWidth ?? 0))
    {
        this.keyword = keyword;
        this.name = name;
        this.equalsToken = equalsToken;
        this.address = address;
        this.signature = signature;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.equalsToken,
        3 => this.address,
        4 => this.signature,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ExternProcDeclarationSyntax(tree, parent, this, position);
}
