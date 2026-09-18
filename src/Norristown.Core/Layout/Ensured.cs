using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// What an <c>.ensure</c> writes: the <c>rep</c> that makes the widths it names 16 bits and
/// the <c>sep</c> that makes them 8, each only where the analysis did not find the width
/// already holding. Its effect on the state is the same whatever it writes, so the choice is
/// made once the analysis has settled and never feeds back into it.
/// </summary>
/// <param name="Reset">The flags the <c>rep</c> clears, or zero when there is none.</param>
/// <param name="Set">The flags the <c>sep</c> sets, or zero when there is none.</param>
public readonly record struct Ensured(int Reset, int Set)
{
    /// <summary>How many bytes it writes: two for each instruction.</summary>
    public int Length => (Reset != 0 ? 2 : 0) + (Set != 0 ? 2 : 0);

    /// <summary>
    /// What <paramref name="directive"/> writes where <paramref name="before"/> reaches it. A
    /// width that is not known there, or a directive nothing reaches, is set all the same.
    /// </summary>
    public static Ensured Of(EnsureDirectiveSyntax directive, ProcessorState? before)
    {
        var reset = 0;
        var set = 0;
        foreach (var item in StateItem.Read(directive))
        {
            if (item.Part is not (StatePart.A or StatePart.Index) || item.Width is not (Width.Eight or Width.Sixteen))
                continue;
            var flag = item.Part == StatePart.A ? 0x20 : 0x10;
            var here = item.Part == StatePart.A ? before?.A : before?.Index;
            if (here == item.Width)
                continue;
            if (item.Width == Width.Sixteen)
                reset |= flag;
            else
                set |= flag;
        }
        return new Ensured(reset, set);
    }
}
