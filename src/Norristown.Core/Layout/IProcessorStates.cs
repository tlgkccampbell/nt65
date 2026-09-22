using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// What the 65816's state is where each statement stands, which is what sizes an immediate,
/// times an instruction and gives a frame slot its offset.
/// <para>
/// Only the flow analysis works that out, and the flow analysis reads a layout to do it: the
/// file is laid out once with nothing here, which is enough to find where control goes, and
/// again with the analysis that first walk made possible. So layout asks for the state through
/// this rather than naming the analysis, and the dependency runs one way.
/// </para>
/// </summary>
public interface IProcessorStates
{
    /// <summary>
    /// The state reaching <paramref name="statement"/> on the writing <paramref name="on"/>,
    /// or null where nothing reaches it or it is in no routine.
    /// </summary>
    /// <param name="statement">The statement.</param>
    /// <param name="on">The writing of it being asked about.</param>
    /// <returns>The state reaching it, or null.</returns>
    ProcessorState? Before(SyntaxNode statement, Expansion? on);

    /// <summary>
    /// The <c>n</c> of <c>n,s</c> that the frame slot in <paramref name="statement"/>'s operand
    /// comes to on the writing <paramref name="on"/>, or null where it names no slot or the
    /// slot is not known.
    /// </summary>
    /// <param name="statement">The statement.</param>
    /// <param name="on">The writing of it being asked about.</param>
    /// <returns>The slot, or null.</returns>
    int? SlotAt(SyntaxNode statement, Expansion? on);
}
