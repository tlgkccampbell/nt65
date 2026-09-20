// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.FuncDeclarationSyntax"/>.
/// <c>.func name(a, b) = expr</c>: a pure expression function.
/// </summary>
internal sealed class FuncDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly ParameterListSyntax parameters;
    private readonly GreenToken equalsToken;
    private readonly ExpressionSyntax body;

    internal FuncDeclarationSyntax(
        GreenToken keyword,
        GreenToken name,
        ParameterListSyntax parameters,
        GreenToken equalsToken,
        ExpressionSyntax body)
        : base(SyntaxKind.FuncDeclaration, keyword.FullWidth + name.FullWidth + parameters.FullWidth + equalsToken.FullWidth + body.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
        this.parameters = parameters;
        this.equalsToken = equalsToken;
        this.body = body;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.parameters,
        3 => this.equalsToken,
        4 => this.body,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.FuncDeclarationSyntax(tree, parent, this, position);
}
