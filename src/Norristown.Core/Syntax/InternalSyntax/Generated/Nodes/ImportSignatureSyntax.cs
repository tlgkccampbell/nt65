// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ImportSignatureSyntax"/>.
/// <c>proc(entry -&gt; exit)</c>: the signature of an imported routine.
/// </summary>
internal sealed class ImportSignatureSyntax : GreenNode
{
    private readonly GreenToken procKeyword;
    private readonly GreenToken openParenToken;
    private readonly GreenNode? entry;
    private readonly GreenToken? arrowToken;
    private readonly GreenNode? exit;
    private readonly GreenToken closeParenToken;

    internal ImportSignatureSyntax(
        GreenToken procKeyword,
        GreenToken openParenToken,
        GreenNode? entry,
        GreenToken? arrowToken,
        GreenNode? exit,
        GreenToken closeParenToken)
        : base(SyntaxKind.ImportSignature, procKeyword.FullWidth + openParenToken.FullWidth + (entry?.FullWidth ?? 0) + (arrowToken?.FullWidth ?? 0) + (exit?.FullWidth ?? 0) + closeParenToken.FullWidth)
    {
        this.procKeyword = procKeyword;
        this.openParenToken = openParenToken;
        this.entry = entry;
        this.arrowToken = arrowToken;
        this.exit = exit;
        this.closeParenToken = closeParenToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 6;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.procKeyword,
        1 => this.openParenToken,
        2 => this.entry,
        3 => this.arrowToken,
        4 => this.exit,
        5 => this.closeParenToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ImportSignatureSyntax(tree, parent, this, position);
}
