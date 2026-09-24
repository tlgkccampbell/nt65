using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents what one register may hold at a point. It may hold the value some register was
/// entered with, a value an instruction in the routine wrote, or something nothing is known
/// about. It is a set rather than one answer, because two paths arriving at a place may leave
/// different things in a register. What matters is whether every one of them is the entry
/// value.
/// <para>
/// The entry value is named by the register it came from rather than by the register holding
/// it, because a 6502 saves X by way of A. After <c>txa</c> the accumulator holds what X was
/// entered with, and after <c>pha</c> so does the byte on the stack.
/// </para>
/// </summary>
/// <param name="Entry">The registers whose entry values it may hold.</param>
/// <param name="IsWritten">Whether it may hold something an instruction here wrote.</param>
/// <param name="IsUnknown">Whether it may hold something nothing at all is known about.</param>
public readonly record struct RegisterValue(Registers Entry, bool IsWritten, bool IsUnknown)
{
    /// <summary>Gets a value an instruction in the routine wrote.</summary>
    public static RegisterValue Written => new(Registers.None, true, false);

    /// <summary>Gets a value nothing is known about, such as what a call nt65 cannot follow leaves behind.</summary>
    public static RegisterValue Unknown => new(Registers.None, false, true);

    /// <summary>Returns exactly the value <paramref name="register"/> was entered with.</summary>
    public static RegisterValue Of(Registers register) => new(register, false, false);

    /// <summary>Returns what the register may hold where two paths meet, which is what either path left.</summary>
    public static RegisterValue Merge(RegisterValue a, RegisterValue b) =>
        new(a.Entry | b.Entry, a.IsWritten || b.IsWritten, a.IsUnknown || b.IsUnknown);

    /// <summary>Returns whether this is the entry value of <paramref name="register"/> and nothing else.</summary>
    public bool Holds(Registers register) => Entry == register && !IsWritten && !IsUnknown;
}
