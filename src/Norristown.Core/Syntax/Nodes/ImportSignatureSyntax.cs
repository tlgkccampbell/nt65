using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>proc(entry -&gt; exit)</c>: the signature of an imported routine.</summary>
public sealed class ImportSignatureSyntax : SyntaxNode
{
    internal ImportSignatureSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>proc</c>.</summary>
    public SyntaxToken ProcKeyword => ChildTokens[0];

    /// <summary>The <c>(</c>, or null.</summary>
    public SyntaxToken? OpenParenToken => FirstToken(SyntaxKind.OpenParen);

    /// <summary>The state on entry, or null when it is left out.</summary>
    public StateListSyntax? Entry => ArrowToken is { } arrow ? NodeBefore(arrow) as StateListSyntax : FirstNode<StateListSyntax>();

    /// <summary>The <c>-&gt;</c>, or null.</summary>
    public SyntaxToken? ArrowToken => FirstToken(SyntaxKind.Arrow);

    /// <summary>The state on exit, or null when it is left out.</summary>
    public StateListSyntax? Exit => NodeAfter(ArrowToken) as StateListSyntax;

    /// <summary>The <c>)</c>, or null.</summary>
    public SyntaxToken? CloseParenToken => FirstToken(SyntaxKind.CloseParen);
}
