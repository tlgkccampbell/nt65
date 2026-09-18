using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>An instruction's operand. Which forms an instruction and a CPU allow is decided in layout.</summary>
public abstract class OperandSyntax : SyntaxNode
{
    private protected OperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
