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
/// <param name="Backed">
/// The registers among <paramref name="Kept"/> that it keeps without relying on a routine it
/// calls keeping one that routine does not promise. For a routine that declares <c>keeps</c>,
/// it is what the declaration lists. For one that declares none, it is what its own code
/// keeps, through what the routines it calls back in turn. A caller that relies on a register
/// in <paramref name="Kept"/> but not here relies on a promise someone declined to make.
/// </param>
public readonly record struct RoutineRegisters(Registers Kept, bool Complete, Registers Backed)
{
    /// <summary>Gets the value assumed for a routine not yet worked out, which keeps every register.</summary>
    public static RoutineRegisters Everything => new(Registers.All, true, Registers.All);

    /// <summary>
    /// Gets the value for a routine whose body is not in the program and which declares nothing.
    /// Such a routine is taken to keep no register.
    /// </summary>
    public static RoutineRegisters Nothing => new(Registers.None, false, Registers.None);

    /// <summary>Gets the registers it keeps without promising to, or without anything it calls promising to.</summary>
    public Registers Unbacked => Kept & ~Backed;
}
