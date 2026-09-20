// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ArgumentListSyntax"/>.
/// <c>(a, b)</c>: the arguments of a call or of a macro call.
/// </summary>
internal sealed class ArgumentListSyntax : GreenNode
{
    private readonly GreenToken openParenToken;
    private readonly GreenSeparatedList? arguments;
    private readonly GreenToken closeParenToken;

    internal ArgumentListSyntax(
        GreenToken openParenToken,
        GreenSeparatedList? arguments,
        GreenToken closeParenToken)
        : base(SyntaxKind.ArgumentList, openParenToken.FullWidth + (arguments?.FullWidth ?? 0) + closeParenToken.FullWidth)
    {
        this.openParenToken = openParenToken;
        this.arguments = arguments;
        this.closeParenToken = closeParenToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openParenToken,
        1 => this.arguments,
        2 => this.closeParenToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ArgumentListSyntax(tree, parent, this, position);
}
