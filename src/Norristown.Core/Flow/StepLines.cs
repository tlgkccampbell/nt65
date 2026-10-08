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
}
