// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ParameterSyntax"/>.
/// One parameter of a <c>.func</c>: its name.
/// </summary>
internal sealed class ParameterSyntax : GreenNode
{
    private readonly GreenToken name;

    internal ParameterSyntax(GreenToken name)
        : base(SyntaxKind.Parameter, name.FullWidth)
    {
        this.name = name;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ParameterSyntax(tree, parent, this, position);
}
