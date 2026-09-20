// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.SegmentStatementSyntax"/>.
/// A line that starts with <c>.segment</c>: a declaration, a block's opener or a region line.
/// </summary>
internal abstract class SegmentStatementSyntax : StatementSyntax
{
    private protected SegmentStatementSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
