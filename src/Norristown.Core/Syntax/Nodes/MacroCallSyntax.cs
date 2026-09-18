using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name!(args)</c>, with the <c>{</c> of a trailing block argument when one follows.</summary>
public sealed class MacroCallSyntax : StatementSyntax
{
    internal MacroCallSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The macro's name.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>!</c>.</summary>
    public SyntaxToken BangToken => ChildTokens[1];

    /// <summary>The arguments, or null.</summary>
    public ArgumentListSyntax? Arguments => FirstNode<ArgumentListSyntax>();

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);
}
