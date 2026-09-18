using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A mnemonic and its operand, if it takes one.</summary>
public sealed class InstructionStatementSyntax : StatementSyntax
{
    internal InstructionStatementSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The mnemonic.</summary>
    public SyntaxToken Mnemonic => ChildTokens[0];

    /// <summary>The operand, or null for an instruction written without one.</summary>
    public OperandSyntax? Operand => FirstNode<OperandSyntax>();
}
