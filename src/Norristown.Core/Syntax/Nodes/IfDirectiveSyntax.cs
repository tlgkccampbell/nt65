using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.if expr {</c>.</summary>
public sealed class IfDirectiveSyntax : ConditionalDirectiveSyntax
{
    internal IfDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
