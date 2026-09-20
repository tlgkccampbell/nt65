// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A label, and the instruction, data directive or macro call written after it, if any.</summary>
public sealed class LabeledLineSyntax : StatementSyntax
{
    internal LabeledLineSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The label.</summary>
    public LabelSyntax Label => Green is GreenSyntax ? (LabelSyntax)ChildNodes[0] : SlotNode<LabelSyntax>(0);

    /// <summary>What is written after the label, or null.</summary>
    public StatementSyntax? Statement =>
        Green is GreenSyntax ? ChildNodes.Length > 1 ? ChildNodes[1] as StatementSyntax : null : SlotNodeOrNull<StatementSyntax>(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitLabeledLine(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitLabeledLine(this);
}
