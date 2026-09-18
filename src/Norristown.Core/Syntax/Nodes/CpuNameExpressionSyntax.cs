using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A CPU's name, where an expression is read.</summary>
public sealed class CpuNameExpressionSyntax : LiteralExpressionSyntax
{
    internal CpuNameExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
