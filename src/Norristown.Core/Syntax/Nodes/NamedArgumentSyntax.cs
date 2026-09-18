using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name = argument</c>: a macro argument given by name.</summary>
public sealed class NamedArgumentSyntax : SyntaxNode
{
    internal NamedArgumentSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The parameter's name.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>=</c>.</summary>
    public SyntaxToken EqualsToken => ChildTokens[1];

    /// <summary>What the parameter is given, or null.</summary>
    public SyntaxNode? Value => ChildNodes.Length > 0 ? ChildNodes[0] : null;
}
