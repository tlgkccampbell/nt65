using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.config NAME = value</c>: a setting, whose value the build may give instead.</summary>
public sealed class ConfigDeclarationSyntax : StatementSyntax
{
    internal ConfigDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.config</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The setting's name, or null.</summary>
    public SyntaxToken? Name => TokenAt(1) is { Kind: SyntaxKind.Identifier } name ? name : null;

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => FirstToken(SyntaxKind.Equals);

    /// <summary>The setting's value, or null.</summary>
    public ExpressionSyntax? Value => FirstNode<ExpressionSyntax>();
}
