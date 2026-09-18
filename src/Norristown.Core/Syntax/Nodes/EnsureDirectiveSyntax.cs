using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.ensure a16, i8</c>: the widths to make hold.</summary>
public sealed class EnsureDirectiveSyntax : StateListDirectiveSyntax
{
    internal EnsureDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
