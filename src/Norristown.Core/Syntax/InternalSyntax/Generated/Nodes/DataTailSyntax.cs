// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.DataTailSyntax"/>.
/// Where a data directive's values are: the body it opens, one braced value, or the values on its line.
/// </summary>
internal abstract class DataTailSyntax : GreenNode
{
    private protected DataTailSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
