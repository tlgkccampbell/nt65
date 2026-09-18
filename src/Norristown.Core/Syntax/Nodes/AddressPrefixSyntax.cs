using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c> before an address.</summary>
public sealed class AddressPrefixSyntax : SyntaxNode
{
    internal AddressPrefixSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The letter.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>:</c>.</summary>
    public SyntaxToken ColonToken => ChildTokens[1];
}
