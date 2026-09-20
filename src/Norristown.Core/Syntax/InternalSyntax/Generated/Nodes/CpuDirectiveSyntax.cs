// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.CpuDirectiveSyntax"/>.
/// <c>.cpu name</c>.
/// </summary>
internal sealed class CpuDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken cpu;

    internal CpuDirectiveSyntax(
        GreenToken keyword,
        GreenToken cpu)
        : base(SyntaxKind.CpuDirective, keyword.FullWidth + cpu.FullWidth)
    {
        this.keyword = keyword;
        this.cpu = cpu;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.cpu,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.CpuDirectiveSyntax(tree, parent, this, position);
}
