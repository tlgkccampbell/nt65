using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>The <c>a</c> of <c>asl a</c>.</summary>
public sealed class AccumulatorOperandSyntax : OperandSyntax
{
    internal AccumulatorOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>a</c>.</summary>
    public SyntaxToken Register => ChildTokens[0];
}
