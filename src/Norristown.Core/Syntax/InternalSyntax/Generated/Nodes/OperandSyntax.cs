// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.OperandSyntax"/>.
/// An instruction's operand. Which forms an instruction and a CPU allow is decided in layout.
/// </summary>
internal abstract class OperandSyntax : GreenNode
{
    private protected OperandSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
