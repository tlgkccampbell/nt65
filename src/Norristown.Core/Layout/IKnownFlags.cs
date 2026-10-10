using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Provides what the flag analysis knows before each statement. Layout needs it to decide what an
/// <c>.ensure</c> emits, and to count an instruction whose cycles depend on a flag or on a
/// register's value.
/// <para>
/// The flag analysis needs a layout to run, so the file is laid out again with its answers.
/// Layout asks for them through this interface rather than referring to the analysis directly,
/// so that the dependency runs only one way, as it does for <see cref="IProcessorStates"/>.
/// </para>
/// </summary>
public interface IKnownFlags
{
    /// <summary>
    /// Returns the flags known just before <paramref name="statement"/> in the
    /// <see cref="Expansion"/> <paramref name="on"/>, or null where no path reaches it.
    /// </summary>
    /// <param name="statement">The statement.</param>
    /// <param name="on">The expansion of the statement being asked about.</param>
    /// <returns>The flags known there, or null.</returns>
    FlagValues? Known(SyntaxNode statement, Expansion? on);

    /// <summary>
    /// Returns the constant the accumulator holds just before <paramref name="statement"/> in the
    /// <see cref="Expansion"/> <paramref name="on"/>, or null where it is not known. On the
    /// 65816 the constant is all 16 bits of C only where A is 16 bits wide there.
    /// </summary>
    /// <param name="statement">The statement.</param>
    /// <param name="on">The expansion of the statement being asked about.</param>
    /// <returns>The constant, or null.</returns>
    long? Accumulator(SyntaxNode statement, Expansion? on);
}
