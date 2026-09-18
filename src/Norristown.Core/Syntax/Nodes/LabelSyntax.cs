using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name:</c> at the start of a line.</summary>
public sealed class LabelSyntax : SyntaxNode
{
    internal LabelSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The label's name.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>:</c>.</summary>
    public SyntaxToken ColonToken => ChildTokens[1];
}
