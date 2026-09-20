// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.CallExpressionSyntax"/>.
/// <c>name(args)</c> or <c>.function(args)</c>.
/// </summary>
internal sealed class CallExpressionSyntax : ExpressionSyntax
{
    private readonly GreenNode? callee;
    private readonly GreenToken? function;
    private readonly GreenNode arguments;

    internal CallExpressionSyntax(
        GreenNode? callee,
        GreenToken? function,
        GreenNode arguments)
        : base(SyntaxKind.CallExpression, (callee?.FullWidth ?? 0) + (function?.FullWidth ?? 0) + arguments.FullWidth)
    {
        this.callee = callee;
        this.function = function;
        this.arguments = arguments;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.callee,
        1 => this.function,
        2 => this.arguments,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.CallExpressionSyntax(tree, parent, this, position);
}
