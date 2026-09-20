// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>The line that opens an <c>.enum</c>, <c>.struct</c>, <c>.union</c>, <c>.charmap</c> or <c>.list</c>.</summary>
public abstract class TypeDeclarationSyntax : StatementSyntax
{
    private protected TypeDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.enum</c>, <c>.struct</c> or the like that starts the line.</summary>
    public abstract SyntaxToken Keyword { get; }

    /// <summary>The declared name, or null for an anonymous declaration.</summary>
    public abstract SyntaxToken? Name { get; }

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public abstract SyntaxToken? OpenBraceToken { get; }
}
