// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ProcSignatureSyntax"/>.
/// The <c>: entry -&gt; exit</c> of a proc, an extern proc or a macro.
/// </summary>
internal sealed class ProcSignatureSyntax : GreenNode
{
    private readonly GreenToken colonToken;
    private readonly StateListSyntax entry;
    private readonly GreenToken? arrowToken;
    private readonly StateListSyntax? exit;

    internal ProcSignatureSyntax(
        GreenToken colonToken,
        StateListSyntax entry,
        GreenToken? arrowToken,
        StateListSyntax? exit)
        : base(SyntaxKind.ProcSignature, colonToken.FullWidth + entry.FullWidth + (arrowToken?.FullWidth ?? 0) + (exit?.FullWidth ?? 0))
    {
        this.colonToken = colonToken;
        this.entry = entry;
        this.arrowToken = arrowToken;
        this.exit = exit;
    }

    /// <inheritdoc/>
    public override int SlotCount => 4;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.colonToken,
        1 => this.entry,
        2 => this.arrowToken,
        3 => this.exit,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ProcSignatureSyntax(tree, parent, this, position);
}
