// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.AssertDirectiveSyntax"/>.
/// <c>.assert expr, "message"</c>, whose message may be left out.
/// </summary>
internal sealed class AssertDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenNode condition;
    private readonly GreenToken? commaToken;
    private readonly GreenToken? level;
    private readonly GreenToken? levelCommaToken;
    private readonly GreenToken? message;

    internal AssertDirectiveSyntax(
        GreenToken keyword,
        GreenNode condition,
        GreenToken? commaToken,
        GreenToken? level,
        GreenToken? levelCommaToken,
        GreenToken? message)
        : base(SyntaxKind.AssertDirective, keyword.FullWidth + condition.FullWidth + (commaToken?.FullWidth ?? 0) + (level?.FullWidth ?? 0) + (levelCommaToken?.FullWidth ?? 0) + (message?.FullWidth ?? 0))
    {
        this.keyword = keyword;
        this.condition = condition;
        this.commaToken = commaToken;
        this.level = level;
        this.levelCommaToken = levelCommaToken;
        this.message = message;
    }

    /// <inheritdoc/>
    public override int SlotCount => 6;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.condition,
        2 => this.commaToken,
        3 => this.level,
        4 => this.levelCommaToken,
        5 => this.message,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.AssertDirectiveSyntax(tree, parent, this, position);
}
