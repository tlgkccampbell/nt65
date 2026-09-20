// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name</c>, <c>name: kind</c>, <c>name = default</c> or all three.</summary>
public sealed class MacroParameterSyntax : SyntaxNode
{
    internal MacroParameterSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The parameter's name.</summary>
    public SyntaxToken Name => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>:</c> before the kind, or null.</summary>
    public SyntaxToken? ColonToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Colon) : SlotTokenOrNull(1);

    /// <summary>What the parameter takes, or null.</summary>
    public ParameterKindSyntax? ParameterKind =>
        Green is GreenSyntax ? FirstNode<ParameterKindSyntax>() : SlotNodeOrNull<ParameterKindSyntax>(2);

    /// <summary>The <c>=</c> before the default, or null.</summary>
    public SyntaxToken? EqualsToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Equals) : SlotTokenOrNull(3);

    /// <summary>The default: an expression, or the <c>{}</c> of a block parameter. Null when there is none.</summary>
    public SyntaxNode? Default => Green is GreenSyntax ? NodeAfter(EqualsToken) : SlotNodeOrNull<SyntaxNode>(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitMacroParameter(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitMacroParameter(this);
}
