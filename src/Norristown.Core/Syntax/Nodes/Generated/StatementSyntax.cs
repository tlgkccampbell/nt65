// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// What a line's tokens parse to: a declaration, a directive, an instruction, a member of a body.
/// A few of them are also written inside another statement, as an instruction is after a label
/// and a data directive after <c>.data name:</c>. A statement is only its own tokens wherever it
/// is written: the line break, whatever the statement could not take and the <c>.export</c>
/// before a declaration all belong to the line (<see cref="LineSyntax"/>).
/// </summary>
public abstract class StatementSyntax : SyntaxNode
{
    private protected StatementSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>Whether this is a declaration written after <c>.export</c>.</summary>
    public bool IsExported => ExportToken is not null;

    /// <summary>The <c>.export</c> the line writes before this declaration, or null.</summary>
    public SyntaxToken? ExportToken => (Parent as LineSyntax)?.ExportKeyword;
}
