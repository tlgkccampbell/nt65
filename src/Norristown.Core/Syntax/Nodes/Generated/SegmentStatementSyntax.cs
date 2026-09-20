// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A line that starts with <c>.segment</c>: a declaration, a block's opener or a region line.</summary>
public abstract class SegmentStatementSyntax : StatementSyntax
{
    private protected SegmentStatementSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.segment</c> that starts the line.</summary>
    public abstract SyntaxToken Keyword { get; }

    /// <summary>The segment's name, or null. A quoted name is an error, and is still the name.</summary>
    public abstract SyntaxToken? Name { get; }
}
