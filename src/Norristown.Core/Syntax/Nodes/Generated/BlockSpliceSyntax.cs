// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A name on its own line, which splices a <c>block</c> parameter.</summary>
public sealed class BlockSpliceSyntax : StatementSyntax
{
    internal BlockSpliceSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The parameter spliced.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitBlockSplice(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitBlockSplice(this);
}
