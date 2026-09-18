using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A line the parser could not read as any statement: its tokens, as they are.</summary>
public sealed class ErrorLineSyntax : StatementSyntax
{
    internal ErrorLineSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
