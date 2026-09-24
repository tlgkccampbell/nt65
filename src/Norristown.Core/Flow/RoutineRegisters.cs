using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents what a routine does to the registers, worked out across the program.
/// </summary>
/// <param name="Kept">
/// The registers it returns holding the values it was entered with, on every path out of it.
/// It is a lower bound, never a guess. A routine may preserve more than this, but never fewer.
/// </param>
/// <param name="Complete">
/// Whether every call it makes was one nt65 could follow. Where it is false the routine may
/// keep more than <paramref name="Kept"/> says, and nothing here can tell.
/// </param>
public readonly record struct RoutineRegisters(Registers Kept, bool Complete)
{
    /// <summary>Gets the value assumed for a routine not yet worked out, which keeps every register.</summary>
    public static RoutineRegisters Everything => new(Registers.All, true);

    /// <summary>
    /// Gets the value for a routine whose body is not in the program and which declares nothing.
    /// Such a routine is taken to keep no register.
    /// </summary>
    public static RoutineRegisters Nothing => new(Registers.None, false);
}
