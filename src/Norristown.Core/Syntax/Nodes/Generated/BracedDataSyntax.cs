// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>The one braced value of a counted or record data directive, written on its line.</summary>
public sealed class BracedDataSyntax : DataTailSyntax
{
    internal BracedDataSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The value: a <see cref="ValueListSyntax"/> or a <see cref="RecordValuesSyntax"/>.</summary>
    public SyntaxNode Value => SlotNode<SyntaxNode>(0);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitBracedData(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitBracedData(this);
}
