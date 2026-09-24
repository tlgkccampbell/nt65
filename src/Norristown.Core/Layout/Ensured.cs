using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Represents the instructions an <c>.ensure</c> emits. A <c>rep</c> makes the widths it names
/// 16 bits and a <c>sep</c> makes them 8 bits, and each is emitted only where the analysis did
/// not find that width already in effect. The directive's effect on the processor state does not
/// depend on which instructions it emits, so the choice is made once the analysis has reached a
/// fixed point and never feeds back into it.
/// </summary>
/// <param name="Reset">The flags the <c>rep</c> clears, or zero when there is none.</param>
/// <param name="Set">The flags the <c>sep</c> sets, or zero when there is none.</param>
public readonly record struct Ensured(int Reset, int Set)
{
    /// <summary>Gets the number of bytes the directive emits, which is two for each instruction.</summary>
    public int Length => (Reset != 0 ? 2 : 0) + (Set != 0 ? 2 : 0);

    /// <summary>
    /// Returns the instructions <paramref name="directive"/> emits when the state reaching it is
    /// <paramref name="before"/>. A width that is not known there is always set, as is every
    /// width of a directive that no state reaches (a null <paramref name="before"/>).
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
