// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.MacroDeclarationSyntax"/>.
/// <c>.macro name(params): entry -&gt; exit {</c>.
/// </summary>
internal sealed class MacroDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly MacroParameterListSyntax parameters;
    private readonly ProcSignatureSyntax? signature;
    private readonly GreenToken openBraceToken;

    internal MacroDeclarationSyntax(
        GreenToken keyword,
        GreenToken name,
        MacroParameterListSyntax parameters,
        ProcSignatureSyntax? signature,
        GreenToken openBraceToken)
        : base(SyntaxKind.MacroDeclaration, keyword.FullWidth + name.FullWidth + parameters.FullWidth + (signature?.FullWidth ?? 0) + openBraceToken.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
        this.parameters = parameters;
        this.signature = signature;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.parameters,
        3 => this.signature,
        4 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.MacroDeclarationSyntax(tree, parent, this, position);
}
