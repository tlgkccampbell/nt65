// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>The line that opens a repetition: <c>.repeat count, name {</c> or <c>.each what, name {</c>.</summary>
public abstract class RepetitionDirectiveSyntax : StatementSyntax
{
    private protected RepetitionDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.repeat</c> or <c>.each</c> that starts the line.</summary>
    public abstract SyntaxToken Keyword { get; }

    /// <summary>The count of a <c>.repeat</c>, or what an <c>.each</c> goes through.</summary>
    public abstract ExpressionSyntax Expression { get; }

    /// <summary>The <c>,</c> before the name, or null.</summary>
    public abstract SyntaxToken? CommaToken { get; }

    /// <summary>The name bound to the index or the item, or null when it is left out.</summary>
    public abstract SyntaxToken? Name { get; }

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public abstract SyntaxToken? OpenBraceToken { get; }
}
