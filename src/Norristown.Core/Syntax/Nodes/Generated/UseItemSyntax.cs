// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>One name in the braces of a <c>.use</c>, and the name it is brought in as.</summary>
public sealed class UseItemSyntax : SyntaxNode
{
    internal UseItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The name used.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>as</c>, or null.</summary>
    public SyntaxToken? AsKeyword => TokenAt(1);

    /// <summary>The name it is brought in as, or null.</summary>
    public SyntaxToken? Alias => TokenAt(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitUseItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitUseItem(this);
}
