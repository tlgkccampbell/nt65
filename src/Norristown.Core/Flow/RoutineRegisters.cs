using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// What a routine does to the registers, worked out across the program.
/// </summary>
/// <param name="Kept">
/// The registers it returns holding the values it was entered with, on every path out of it.
/// It is a lower bound and never a guess: a routine may preserve more than this, never fewer.
/// </param>
/// <param name="Complete">
/// Whether every call it makes was one nt65 could follow. Where it is false the routine may
/// keep more than <paramref name="Kept"/> says, and nothing here can tell.
/// </param>
public readonly record struct RoutineRegisters(Registers Kept, bool Complete)
{
    /// <summary>What a routine that has not been worked out yet is taken to keep: everything.</summary>
    public static RoutineRegisters Everything => new(Registers.All, true);

    /// <summary>What is known of a routine whose body is not here and which declares nothing: nothing.</summary>
    public static RoutineRegisters Nothing => new(Registers.None, false);
}
