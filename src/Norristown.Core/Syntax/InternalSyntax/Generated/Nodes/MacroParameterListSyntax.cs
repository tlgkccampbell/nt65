// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.MacroParameterListSyntax"/>.
/// <c>(a, b: expr, c = 1)</c>: the parameters of a macro.
/// </summary>
internal sealed class MacroParameterListSyntax : GreenNode
{
    private readonly GreenToken openParenToken;
    private readonly GreenSeparatedList? parameters;
    private readonly GreenToken closeParenToken;

    internal MacroParameterListSyntax(
        GreenToken openParenToken,
        GreenSeparatedList? parameters,
        GreenToken closeParenToken)
        : base(SyntaxKind.MacroParameterList, openParenToken.FullWidth + (parameters?.FullWidth ?? 0) + closeParenToken.FullWidth)
    {
        this.openParenToken = openParenToken;
        this.parameters = parameters;
        this.closeParenToken = closeParenToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openParenToken,
        1 => this.parameters,
        2 => this.closeParenToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.MacroParameterListSyntax(tree, parent, this, position);
}
