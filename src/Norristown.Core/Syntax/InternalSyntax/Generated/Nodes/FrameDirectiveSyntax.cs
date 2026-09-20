// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.FrameDirectiveSyntax"/>.
/// <c>.frame locals: Locals</c>: a name, and the struct the top of the stack is laid out as.
/// </summary>
internal sealed class FrameDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly GreenToken colonToken;
    private readonly GreenNode type;

    internal FrameDirectiveSyntax(
        GreenToken keyword,
        GreenToken name,
        GreenToken colonToken,
        GreenNode type)
        : base(SyntaxKind.FrameDirective, keyword.FullWidth + name.FullWidth + colonToken.FullWidth + type.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
        this.colonToken = colonToken;
        this.type = type;
    }

    /// <inheritdoc/>
    public override int SlotCount => 4;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.colonToken,
        3 => this.type,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.FrameDirectiveSyntax(tree, parent, this, position);
}
