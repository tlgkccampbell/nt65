using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.segment NAME</c>: a region line, which puts what follows it in the segment.</summary>
public sealed class SegmentRegionSyntax : SegmentStatementSyntax
{
    internal SegmentRegionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
