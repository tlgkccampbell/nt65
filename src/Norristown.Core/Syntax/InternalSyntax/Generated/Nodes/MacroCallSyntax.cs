// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.MacroCallSyntax"/>.
/// <c>name!(args)</c>, with the <c>{</c> of a trailing block argument when one follows.
/// </summary>
internal sealed class MacroCallSyntax : StatementSyntax
{
    private readonly GreenToken name;
    private readonly GreenToken bangToken;
    private readonly GreenNode arguments;
    private readonly GreenToken? openBraceToken;

    internal MacroCallSyntax(
        GreenToken name,
        GreenToken bangToken,
        GreenNode arguments,
        GreenToken? openBraceToken)
        : base(SyntaxKind.MacroCall, name.FullWidth + bangToken.FullWidth + arguments.FullWidth + (openBraceToken?.FullWidth ?? 0))
    {
        this.name = name;
        this.bangToken = bangToken;
        this.arguments = arguments;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 4;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.bangToken,
        2 => this.arguments,
        3 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.MacroCallSyntax(tree, parent, this, position);
}
