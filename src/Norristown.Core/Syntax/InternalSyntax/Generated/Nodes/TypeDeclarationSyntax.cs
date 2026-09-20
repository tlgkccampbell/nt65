// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.TypeDeclarationSyntax"/>.
/// The line that opens an <c>.enum</c>, <c>.struct</c>, <c>.union</c>, <c>.charmap</c> or <c>.list</c>.
/// </summary>
internal abstract class TypeDeclarationSyntax : StatementSyntax
{
    private protected TypeDeclarationSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
