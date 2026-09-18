using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.state a16, i8</c>: the items of a signature, asserted and set at one point.</summary>
public sealed class StateDirectiveSyntax : StateListDirectiveSyntax
{
    internal StateDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
