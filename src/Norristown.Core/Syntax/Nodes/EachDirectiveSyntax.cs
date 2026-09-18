using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.each what, name {</c>.</summary>
public sealed class EachDirectiveSyntax : RepetitionDirectiveSyntax
{
    internal EachDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
