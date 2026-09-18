using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A line holding nothing, or nothing but a comment.</summary>
public sealed class BlankLineSyntax : StatementSyntax
{
    internal BlankLineSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
