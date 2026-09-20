// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.RepetitionDirectiveSyntax"/>.
/// The line that opens a repetition: <c>.repeat count, name {</c> or <c>.each what, name {</c>.
/// </summary>
internal abstract class RepetitionDirectiveSyntax : StatementSyntax
{
    private protected RepetitionDirectiveSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
