using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents one <c>.allow</c>, which keeps the warning it names from being reported on the lines
/// of the statement below it.
/// </summary>
/// <param name="Directive">The <c>.allow</c> line.</param>
/// <param name="Name">The name of the warning it allows.</param>
/// <param name="Covered">
/// The lines of the statement it applies to, which are every line of a block the statement
/// opens.
/// </param>
/// <param name="InMacroBody">
/// Whether a macro body holds the <c>.allow</c>. One there may be needed only for some calls, so
/// it is not reported when it hides nothing.
/// </param>
public sealed record Allowance(AllowDirectiveSyntax Directive, string Name, LineRange Covered, bool InMacroBody)
{
    /// <summary>
    /// Returns the lines of the statement that the <c>.allow</c> on <paramref name="line"/>
    /// applies to, or null when nothing follows it in its block. The statement is the next sibling
    /// that is not a blank line or another <c>.allow</c>. When that sibling is a block, such as a
    /// <c>.proc</c> or a macro call with a block argument, every line of the block is covered.
    /// </summary>
    public static LineRange? CoveredBy(LineSyntax line)
    {
        if (line.Parent is not { } container)
            return null;
        var siblings = container.ChildNodes;
        for (var i = siblings.IndexOf(line) + 1; i < siblings.Length; i++)
        {
            switch (siblings[i])
            {
                case LineSyntax { Statement: BlankLineSyntax or AllowDirectiveSyntax }:
                    continue;
                case LineSyntax { Statement: BlockCloseLineSyntax or BlockContinuationSyntax }:
                    return null;
                case var next:
                    return LinesOf(next);
            }
        }
        return null;
    }

    /// <summary>Returns the lines <paramref name="node"/> spans.</summary>
    private static LineRange LinesOf(SyntaxNode node)
    {
        var tree = node.Tree;
        var span = node.Span;
        return new LineRange(tree.GetLineIndex(span.Start), tree.GetLineIndex(Math.Max(span.Start, span.End - 1)));
    }
}
