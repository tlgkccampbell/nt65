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
    public LabelSyntax Label => (LabelSyntax)ChildNodes[0];

    /// <summary>What is written after the label, or null.</summary>
    public StatementSyntax? Statement => ChildNodes.Length > 1 ? ChildNodes[1] as StatementSyntax : null;
}
