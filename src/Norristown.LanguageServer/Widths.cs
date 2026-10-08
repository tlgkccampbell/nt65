using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the <c>nt65/widths</c> request. It converts the states that <see cref="WidthStripes"/>
/// finds on the lines of every routine into runs of lines, for the client to draw as stripes
/// beside the line numbers.
/// <para>
/// A line with no step of its own, such as a comment or a blank line, takes the state of the next
/// line that has one, which is what the code below it runs with. So the lines that open a routine
/// have the state the routine is entered with. Lines after the routine's last step have none.
/// </para>
/// </summary>
internal static class Widths
{
    /// <summary>
    /// Returns the runs of lines in <paramref name="model"/>'s file on which something is known
    /// of the widths or the mode. It returns null where the file has no processor state, which is
    /// so on every processor but the 65816.
    /// </summary>
    public static Protocol.WidthsResult? Of(ProgramAnalysis analysis, SemanticModel model)
    {
        if (analysis.StatesFor(model.Tree.Path) is null)
            return null;
        var tree = model.Tree;
        var lines = new Protocol.WidthRun?[tree.LineCount];

        // Where one routine's text holds another's, the inner routine's lines are its own, so the
        // longer routines are drawn first and the shorter ones over them.
        foreach (var routine in WidthStripes.In(analysis, model).OrderByDescending(routine => routine.Routine.Length))
            Fill(tree, routine, lines);

        var runs = new List<Protocol.WidthRun>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] is not { } state)
                continue;
            var last = i;
            while (last + 1 < lines.Length && lines[last + 1] == state)
                last++;
            runs.Add(state with { First = i, Last = last });
            i = last;
        }
        return new Protocol.WidthsResult(runs);
    }

    /// <summary>Sets the state of each line of one routine that has one.</summary>
    private static void Fill(SyntaxTree tree, WidthStripes routine, Protocol.WidthRun?[] lines)
    {
        var line = tree.GetLineIndex(routine.Routine.Start);
        foreach (var stripe in routine.Lines)
        {
            var first = tree.GetLineIndex(stripe.Line.Start);
            var last = tree.GetLineIndex(Math.Max(stripe.Line.Start, stripe.Line.End - 1));
            var state = Of(stripe.State);

            // The lines since the last step, and every line of a statement continued over several,
            // run with the state the statement does.
            for (; line <= last; line++)
                lines[line] = state;
        }
    }

    /// <summary>
    /// Returns what the client draws for a state, which says nothing of the lines it is on, or
    /// null where nothing is known of the widths or the mode.
    /// </summary>
    private static Protocol.WidthRun? Of(ProcessorState state)
    {
        if (state.E == ProcessorMode.Emulation)
            return new Protocol.WidthRun(0, 0, null, null, Emulation: true);
        var a = Bits(state.A);
        var index = Bits(state.Index);
        return a is null && index is null ? null : new Protocol.WidthRun(0, 0, a, index, Emulation: false);
    }

    /// <summary>Returns a width in bits, or null where it is not known.</summary>
    private static int? Bits(Width width) => width switch
    {
        Width.Eight => 8,
        Width.Sixteen => 16,
        _ => null,
    };
}
