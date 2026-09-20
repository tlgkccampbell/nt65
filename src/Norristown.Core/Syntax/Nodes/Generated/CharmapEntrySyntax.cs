// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>One entry of a <c>.charmap</c>: a character, or a range of them, and a value.</summary>
public sealed class CharmapEntrySyntax : StatementSyntax
{
    internal CharmapEntrySyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The character, or the first of the range.</summary>
    public ExpressionSyntax First => (ExpressionSyntax)ChildNodes[0];

    /// <summary>The <c>..</c>, or null.</summary>
    public SyntaxToken? DotDotToken => FirstToken(SyntaxKind.DotDot);

    /// <summary>The last character of the range, or null.</summary>
    public ExpressionSyntax? Last => DotDotToken is null ? null : (ExpressionSyntax)ChildNodes[1];

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => FirstToken(SyntaxKind.Equals);

    /// <summary>The value the first character maps to.</summary>
    public ExpressionSyntax Value => (ExpressionSyntax)ChildNodes[DotDotToken is null ? 1 : 2];

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitCharmapEntry(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitCharmapEntry(this);
}
