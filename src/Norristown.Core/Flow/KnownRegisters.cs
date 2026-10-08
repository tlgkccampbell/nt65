using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents what the flag analysis knows about A, X and Y at one point: the constant each
/// holds, where the instructions on every path give it one, and which registers N and Z were last
/// set from. Only what the CPU defines is used, so nothing about memory is assumed.
/// <para>
/// A register set from N and Z's own result still holds that result, so N and Z say whether it
/// is zero or negative. A compare with zero there changes only the carry, and a branch that finds
/// Z set there shows the register holds 0.
/// </para>
/// </summary>
/// <param name="A">The constant the accumulator holds, or null where it is not known.</param>
/// <param name="X">The constant X holds, or null where it is not known.</param>
/// <param name="Y">The constant Y holds, or null where it is not known.</param>
/// <param name="NzFrom">The registers whose value N and Z were last set from, and that still hold it.</param>
internal readonly record struct KnownRegisters(long? A, long? X, long? Y, Registers NzFrom)
{
    /// <summary>Gets the state in which nothing is known about any register.</summary>
    public static KnownRegisters Unknown => default;

    /// <summary>Returns the constant <paramref name="register"/> holds, or null where it is not known.</summary>
    public long? ValueOf(Registers register) => register switch
    {
        Registers.A => A,
        Registers.X => X,
        _ => Y,
    };

    /// <summary>
    /// Returns this state with <paramref name="register"/> holding <paramref name="value"/>, or
    /// holding something not known where it is null.
    /// </summary>
    public KnownRegisters With(Registers register, long? value) => register switch
    {
        Registers.A => this with { A = value },
        Registers.X => this with { X = value },
        _ => this with { Y = value },
    };

    /// <summary>
    /// Returns this state after an instruction writes <paramref name="written"/> with values
    /// nothing here can know. Those registers no longer hold what N and Z were set from.
    /// </summary>
    public KnownRegisters Forget(Registers written)
    {
        var state = this with { NzFrom = NzFrom & ~written };
        foreach (var register in (Registers[])[Registers.A, Registers.X, Registers.Y])
        {
            if ((written & register) != 0)
                state = state.With(register, null);
        }
        return state;
    }

    /// <summary>
    /// Returns this state on a path where a branch found Z to be 1, where every register N and Z
    /// were set from holds 0.
    /// </summary>
    public KnownRegisters LearnZero()
    {
        var state = this;
        foreach (var register in (Registers[])[Registers.A, Registers.X, Registers.Y])
        {
            if ((NzFrom & register) != 0)
                state = state.With(register, 0);
        }
        return state;
    }

    /// <summary>
    /// Returns what is known where this state and <paramref name="other"/> meet. A register keeps
    /// its constant only where both paths agree on it.
    /// </summary>
    public KnownRegisters Merge(KnownRegisters other) => new(
        A == other.A ? A : null,
        X == other.X ? X : null,
        Y == other.Y ? Y : null,
        NzFrom & other.NzFrom);
}
