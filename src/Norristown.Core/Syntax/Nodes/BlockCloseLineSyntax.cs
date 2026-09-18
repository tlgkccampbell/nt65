using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A <c>}</c> on its own line.</summary>
public sealed class BlockCloseLineSyntax : StatementSyntax
{
    internal BlockCloseLineSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>}</c>.</summary>
    public SyntaxToken CloseBraceToken => ChildTokens[0];
}
