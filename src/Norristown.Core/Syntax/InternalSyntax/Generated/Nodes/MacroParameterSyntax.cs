// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.MacroParameterSyntax"/>.
/// <c>name</c>, <c>name: kind</c>, <c>name = default</c> or all three.
/// </summary>
internal sealed class MacroParameterSyntax : GreenNode
{
    private readonly GreenToken name;
    private readonly GreenToken? colonToken;
    private readonly GreenNode? parameterKind;
    private readonly GreenToken? equalsToken;
    private readonly GreenNode? @default;

    internal MacroParameterSyntax(
        GreenToken name,
        GreenToken? colonToken,
        GreenNode? parameterKind,
        GreenToken? equalsToken,
        GreenNode? @default)
        : base(SyntaxKind.MacroParameter, name.FullWidth + (colonToken?.FullWidth ?? 0) + (parameterKind?.FullWidth ?? 0) + (equalsToken?.FullWidth ?? 0) + (@default?.FullWidth ?? 0))
    {
        this.name = name;
        this.colonToken = colonToken;
        this.parameterKind = parameterKind;
        this.equalsToken = equalsToken;
        this.@default = @default;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.colonToken,
        2 => this.parameterKind,
        3 => this.equalsToken,
        4 => this.@default,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.MacroParameterSyntax(tree, parent, this, position);
}
