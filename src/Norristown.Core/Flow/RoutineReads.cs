using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents which registers a routine uses the entry values of, worked out across the
/// program. A routine reads a register when some path through it, or through a routine it
/// passes control to, uses the value the register held when the routine was entered. A value
/// moved to another register or saved on the stack and used from there still counts.
/// <para>
/// Anything the analysis does not follow counts as read. A value saved on the stack that is not
/// taken back by the matching pull is read. Where control passes to code nt65 cannot follow,
/// <see cref="Complete"/> is false, and <see cref="Assumed"/> takes every register to be read.
/// What the analysis does follow, <see cref="Read"/> can overstate but never understate.
/// </para>
/// </summary>
/// <param name="Read">The registers whose entry values the routine may use.</param>
/// <param name="Complete">
/// Whether every routine it passes control to was one nt65 could follow, wherever the registers
/// or the stack still held one of its entry values there. Code that cannot be followed sees only
/// the registers and the stack, so where none of them holds an entry value it cannot read one.
/// Where this is false the routine may read more than <paramref name="Read"/> says, and a caller
/// has to take it that every register is read.
/// </param>
public readonly record struct RoutineReads(Registers Read, bool Complete)
{
    /// <summary>Gets the value assumed for a routine not yet worked out, which reads nothing.</summary>
    public static RoutineReads Nothing => new(Registers.None, true);

    /// <summary>
    /// Gets the value for a routine whose body is not in the program. Nothing is known about what
    /// it reads.
    /// </summary>
    public static RoutineReads Unknown => new(Registers.None, false);

    /// <summary>
    /// Gets the registers a caller has to take the routine to read, which is every register where
    /// what it reads is not completely known.
    /// </summary>
    public Registers Assumed => Complete ? Read : Registers.All;
}
