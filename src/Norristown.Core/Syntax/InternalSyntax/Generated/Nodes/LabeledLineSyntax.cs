// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.LabeledLineSyntax"/>.
/// A label, and the instruction, data directive or macro call written after it, if any.
/// </summary>
internal sealed class LabeledLineSyntax : StatementSyntax
{
    private readonly LabelSyntax label;
    private readonly StatementSyntax? statement;

    internal LabeledLineSyntax(
        LabelSyntax label,
        StatementSyntax? statement)
        : base(SyntaxKind.LabeledLine, label.FullWidth + (statement?.FullWidth ?? 0))
    {
        this.label = label;
        this.statement = statement;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.label,
        1 => this.statement,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.LabeledLineSyntax(tree, parent, this, position);
}
