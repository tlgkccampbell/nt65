using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A name on its own line, which splices a <c>block</c> parameter.</summary>
public sealed class BlockSpliceSyntax : StatementSyntax
{
    internal BlockSpliceSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The parameter spliced.</summary>
    public SyntaxToken Name => ChildTokens[0];
}
