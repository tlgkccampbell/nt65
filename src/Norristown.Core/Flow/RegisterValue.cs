using Norristown.Layout;

namespace Norristown.Flow;

/// <summary>
/// What one register may hold at a point: the value some register was entered with, a value
/// an instruction here wrote, or something nothing is known about. It is a set rather than
/// one answer, because two paths arriving at a place may leave different things in a
/// register and what matters is whether every one of them is the entry value.
/// <para>
/// The entry value is named by the register it came from rather than by the register holding
/// it, because a 6502 saves X by way of A: after <c>txa</c> the accumulator holds what X was
/// entered with, and after <c>pha</c> so does the byte on the stack.
/// </para>
/// </summary>
/// <param name="Entry">The registers whose entry values it may hold.</param>
/// <param name="IsWritten">Whether it may hold something an instruction here wrote.</param>
/// <param name="IsUnknown">Whether it may hold something nothing at all is known about.</param>
public readonly record struct RegisterValue(Registers Entry, bool IsWritten, bool IsUnknown)
{
    /// <summary>Something an instruction here wrote.</summary>
    public static RegisterValue Written => new(Registers.None, true, false);

    /// <summary>Something nothing is known about: what a call nt65 cannot follow leaves behind.</summary>
    public static RegisterValue Unknown => new(Registers.None, false, true);

    /// <summary>Exactly the value <paramref name="register"/> was entered with.</summary>
    public static RegisterValue Of(Registers register) => new(register, false, false);

    /// <summary>Whether it is the entry value of <paramref name="register"/> and nothing else.</summary>
    public bool Holds(Registers register) => Entry == register && !IsWritten && !IsUnknown;

    /// <summary>What two paths arriving at one place agree the register may hold: either of them.</summary>
    public static RegisterValue Merge(RegisterValue a, RegisterValue b) =>
        new(a.Entry | b.Entry, a.IsWritten || b.IsWritten, a.IsUnknown || b.IsUnknown);
}
