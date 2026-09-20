// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.SkippedTokensSyntax"/>.
/// What was left on a line after its statement: tokens the parser walked past.
/// </summary>
internal sealed class SkippedTokensSyntax : GreenNode
{
    private readonly GreenList? tokens;

    internal SkippedTokensSyntax(GreenList? tokens)
        : base(SyntaxKind.SkippedTokens, (tokens?.FullWidth ?? 0))
    {
        this.tokens = tokens;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.tokens,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.SkippedTokensSyntax(tree, parent, this, position);
}
