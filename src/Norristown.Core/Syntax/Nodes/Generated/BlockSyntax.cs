// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax;

public sealed partial class BlockSyntax : SyntaxNode
{
    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitBlock(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitBlock(this);
}
