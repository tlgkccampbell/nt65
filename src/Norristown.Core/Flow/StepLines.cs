using Norristown.Layout;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Finds the line of a file that an editor shows a <see cref="Step"/> on. A step in a macro's
/// body is shown on the call that expanded it, because the body is not a line of the file.
/// </summary>
internal static class StepLines
{
    /// <summary>
    /// Returns the span a step is shown at in <paramref name="tree"/>, and whether that is a macro
    /// call standing for a line of the macro's body. It returns null where the step is in no line
    /// of the file. A line a call gave as a block argument is the caller's own, and is shown where
    /// it is.
    /// </summary>
    public static (TextSpan Span, bool InMacro)? Of(SyntaxTree tree, Step step)
    {
        var node = step.Statement;
        var inBody = node.Tree != tree;
        MacroCallSyntax? call = null;
        for (var level = step.On; level is not null; level = level.Outer)
        {
            if (level.Call is null)
                continue;
            call = level.Call;
            if (level.Body is { } body && body.Tree == node.Tree
                && node.Position >= body.Position && node.Position < body.FullSpan.End)
            {
                inBody = true;
            }
        }
        if (!inBody || call is null)
            return node.Tree == tree ? (node.Span, false) : null;
        return call.Tree == tree ? (call.Span, true) : null;
    }

    /// <summary>
    /// Returns the span of each line of <paramref name="tree"/> that one of <paramref name="steps"/>
    /// is shown on, each line once, in the order of the lines.
    /// </summary>
    public static List<TextSpan> Lines(SyntaxTree tree, IEnumerable<Step> steps) =>
        [.. steps.Select(step => Of(tree, step)?.Span).OfType<TextSpan>().Distinct().OrderBy(span => span.Start)];

    /// <summary>
    /// Returns the phrase that names <paramref name="lines"/> of <paramref name="tree"/> as what
    /// might also have changed a value in memory, such as <c>or possibly `sta (ptr),y` on line
    /// 12</c>, or null where there are no such lines.
    /// </summary>
    public static string? OrPossibly(SyntaxTree tree, IReadOnlyList<TextSpan> lines) =>
        lines.Count == 0 ? null : "or possibly " + string.Join(", ", lines.Select(span =>
            $"`{tree.Text[span.Start..span.End].Trim()}` on line {tree.GetLineIndex(span.Start) + 1}"));
}
