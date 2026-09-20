// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ProcDeclarationSyntax"/>.
/// <c>.proc name: entry -&gt; exit {</c>.
/// </summary>
internal sealed class ProcDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly ProcSignatureSyntax? signature;
    private readonly GreenToken openBraceToken;

    internal ProcDeclarationSyntax(
        GreenToken keyword,
        GreenToken name,
        ProcSignatureSyntax? signature,
        GreenToken openBraceToken)
        : base(SyntaxKind.ProcDeclaration, keyword.FullWidth + name.FullWidth + (signature?.FullWidth ?? 0) + openBraceToken.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
        this.signature = signature;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 4;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.signature,
        3 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ProcDeclarationSyntax(tree, parent, this, position);
}
