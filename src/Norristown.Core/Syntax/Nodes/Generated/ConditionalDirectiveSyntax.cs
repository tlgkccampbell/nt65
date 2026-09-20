// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A line that opens a branch with a condition: <c>.if expr {</c> or <c>} .elseif expr {</c>.</summary>
public abstract class ConditionalDirectiveSyntax : StatementSyntax
{
    private protected ConditionalDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.if</c> or <c>.elseif</c>.</summary>
    public abstract SyntaxToken Keyword { get; }

    /// <summary>The condition the branch is taken on.</summary>
    public abstract ExpressionSyntax Condition { get; }

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public abstract SyntaxToken? OpenBraceToken { get; }
}
