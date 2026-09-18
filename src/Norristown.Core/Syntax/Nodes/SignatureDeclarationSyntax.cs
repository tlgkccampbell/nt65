using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.signature std = a8, i16, dp = 0</c>: a name for items a signature uses.</summary>
public sealed class SignatureDeclarationSyntax : StatementSyntax
{
    internal SignatureDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.signature</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The set's name, or null.</summary>
    public SyntaxToken? Name => NameAt(1);

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => FirstToken(SyntaxKind.Equals);

    /// <summary>The items the name stands for, or null.</summary>
    public StateListSyntax? Items => FirstNode<StateListSyntax>();
}
