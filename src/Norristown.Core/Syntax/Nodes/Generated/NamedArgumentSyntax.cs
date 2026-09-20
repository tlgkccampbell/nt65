// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name = argument</c>: a macro argument given by name.</summary>
public sealed class NamedArgumentSyntax : SyntaxNode
{
    internal NamedArgumentSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The parameter's name.</summary>
    public SyntaxToken Name => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>=</c>.</summary>
    public SyntaxToken EqualsToken => Green is GreenSyntax ? ChildTokens[1] : SlotToken(1);

    /// <summary>What the parameter is given, or null.</summary>
    public SyntaxNode? Value =>
        Green is GreenSyntax ? ChildNodes.Length > 0 ? ChildNodes[0] : null : SlotNode<SyntaxNode>(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitNamedArgument(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitNamedArgument(this);
}
