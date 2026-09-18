using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.proc name: entry -&gt; exit {</c>.</summary>
public sealed class ProcDeclarationSyntax : StatementSyntax
{
    internal ProcDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.proc</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The routine's name, or null.</summary>
    public SyntaxToken? Name => NameAt(1);

    /// <summary>The signature, or null.</summary>
    public ProcSignatureSyntax? Signature => FirstNode<ProcSignatureSyntax>();

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);
}
