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
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>:</c> before the kind, or null.</summary>
    public SyntaxToken? ColonToken => FirstToken(SyntaxKind.Colon);

    /// <summary>What the parameter takes, or null.</summary>
    public ParameterKindSyntax? ParameterKind => FirstNode<ParameterKindSyntax>();

    /// <summary>The <c>=</c> before the default, or null.</summary>
    public SyntaxToken? EqualsToken => FirstToken(SyntaxKind.Equals);

    /// <summary>The default: an expression, or the <c>{}</c> of a block parameter. Null when there is none.</summary>
    public SyntaxNode? Default => NodeAfter(EqualsToken);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitMacroParameter(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitMacroParameter(this);
}
