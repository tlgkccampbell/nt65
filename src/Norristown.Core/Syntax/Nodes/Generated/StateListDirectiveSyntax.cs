// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A directive that takes the items of a signature: <c>.state</c> or <c>.ensure</c>.</summary>
public abstract class StateListDirectiveSyntax : StatementSyntax
{
    private protected StateListDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.state</c> or <c>.ensure</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The items.</summary>
    public StateListSyntax Items => FirstNode<StateListSyntax>()!;
}
