using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Provides the 65816's processor state at each statement, which layout needs to size an
/// immediate, time an instruction and give a frame slot its offset.
/// <para>
/// Only the flow analysis works that state out, and the flow analysis needs a layout to do it.
/// The file is laid out once without any state, which is enough to find where control goes, and
/// then again with the analysis that the first layout made possible. Layout therefore asks for
/// the state through this interface rather than referring to the analysis directly, so that the
/// dependency runs only one way.
/// </para>
/// </summary>
public interface IProcessorStates
{
    /// <summary>
    /// Returns the state reaching <paramref name="statement"/> in the <see cref="Expansion"/>
    /// <paramref name="on"/>, or null where nothing reaches it or it is in no routine.
    /// </summary>
    /// <param name="statement">The statement.</param>
    /// <param name="on">The expansion of the statement being asked about.</param>
    /// <returns>The state reaching it, or null.</returns>
    ProcessorState? Before(SyntaxNode statement, Expansion? on);

    /// <summary>
    /// Returns the <c>n</c> of <c>n,s</c> that the frame slot in <paramref name="statement"/>'s
    /// operand resolves to in the <see cref="Expansion"/> <paramref name="on"/>, or null where the
    /// operand names no slot or the slot is not known.
    /// </summary>
    /// <param name="statement">The statement.</param>
    /// <param name="on">The expansion of the statement being asked about.</param>
    /// <returns>The slot, or null.</returns>
    int? SlotAt(SyntaxNode statement, Expansion? on);
}
