// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
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

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitInstructionStatement(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitInstructionStatement(this);
}
