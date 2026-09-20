// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateFlagItemSyntax"/>.
/// A state word and the <c>*</c> or <c>?</c> after it: <c>a16</c>, <c>i*</c>, <c>e?</c>.
/// </summary>
internal sealed class StateFlagItemSyntax : StateItemSyntax
{
    private readonly GreenToken name;
    private readonly GreenToken? suffixToken;

    internal StateFlagItemSyntax(
        GreenToken name,
        GreenToken? suffixToken)
        : base(SyntaxKind.StateFlagItem, name.FullWidth + (suffixToken?.FullWidth ?? 0))
    {
        this.name = name;
        this.suffixToken = suffixToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.suffixToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.StateFlagItemSyntax(tree, parent, this, position);
}
