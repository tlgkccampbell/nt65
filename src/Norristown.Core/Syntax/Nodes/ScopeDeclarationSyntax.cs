using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.scope name {</c>.</summary>
public sealed class ScopeDeclarationSyntax : StatementSyntax
{
    internal ScopeDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.scope</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The scope's name, or null for an anonymous scope.</summary>
    public SyntaxToken? Name => NameAt(1);

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);
}
