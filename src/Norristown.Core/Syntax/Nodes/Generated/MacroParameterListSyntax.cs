// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>(a, b: expr, c = 1)</c>: the parameters of a macro.</summary>
public sealed class MacroParameterListSyntax : SyntaxNode
{
    private ImmutableArray<MacroParameterSyntax> parameters;

    internal MacroParameterListSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>(</c>.</summary>
    public SyntaxToken OpenParenToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The parameters.</summary>
    public ImmutableArray<MacroParameterSyntax> Parameters => Nodes(ref parameters);

    /// <summary>The <c>)</c>, or null.</summary>
    public SyntaxToken? CloseParenToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseParen) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitMacroParameterList(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitMacroParameterList(this);
}
