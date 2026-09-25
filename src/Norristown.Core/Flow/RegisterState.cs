using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents what each register may hold at one point in a routine, and what the routine has
/// pushed.
/// <para>
/// On the 65816 an instruction that works on an 8-bit accumulator leaves its high byte alone, so
/// the two halves are followed apart. <see cref="A"/> is what the low byte holds, and
/// <see cref="AHigh"/> what the high byte holds. <see cref="With"/> writes both, as every CPU but
/// the 65816 always does, and nothing but the 65816's analysis reads <see cref="AHigh"/>.
/// </para>
/// </summary>
/// <param name="A">What the accumulator may hold.</param>
/// <param name="X">What X may hold.</param>
/// <param name="Y">What Y may hold.</param>
/// <param name="C">What the carry may hold.</param>
/// <param name="Stack">What the routine has pushed, or null when that is not known.</param>
public sealed record RegisterState(
    RegisterValue A, RegisterValue X, RegisterValue Y, RegisterValue C, SavedStack? Stack)
{
    /// <summary>
    /// Gets the state of a routine when it is entered, in which each register holds its own entry
    /// value and nothing is pushed.
    /// </summary>
    public static RegisterState Entered { get; } = new(
        RegisterValue.Of(Registers.A), RegisterValue.Of(Registers.X),
        RegisterValue.Of(Registers.Y), RegisterValue.Of(Registers.C), SavedStack.Empty)
    {
        AHigh = RegisterValue.Of(Registers.A),
    };

    /// <summary>
    /// Gets the state where the analysis never saw control arrive, in which nothing is known
    /// about any register or about the stack.
    /// </summary>
    public static RegisterState Unknown { get; } = new(
        RegisterValue.Unknown, RegisterValue.Unknown, RegisterValue.Unknown, RegisterValue.Unknown, null)
    {
        AHigh = RegisterValue.Unknown,
    };

    /// <summary>
    /// Gets the starting state at a label that another routine may jump into, in which nothing is
    /// known about the registers and the stack is empty. Code that jumps in arrives as a call
    /// would, and has pushed none of the saves this routine makes.
    /// </summary>
    public static RegisterState Outside { get; } = Unknown with { Stack = SavedStack.Empty };

    /// <summary>
    /// Gets what the high byte of the 65816's accumulator may hold. Its entry value is part of the
    /// accumulator's, so it is named <see cref="Registers.A"/>.
    /// </summary>
    public RegisterValue AHigh { get; init; }

    /// <summary>Gets why the stack is unknown, when it is unknown and the analysis can tell why.</summary>
    public Cause? WhyStack { get; init; }

    /// <summary>Gets the registers that hold exactly their own entry value here.</summary>
    public Registers Kept
    {
        get
        {
            var kept = Registers.None;
            foreach (var register in RegisterEffects.Each(Registers.All))
            {
                if (Of(register).Holds(register) && (register != Registers.A || AHigh.Holds(register)))
                    kept |= register;
            }
            return kept;
        }
    }

    /// <summary>
    /// Returns what two paths arriving at one place agree on, which is what either of them may
    /// have left.
    /// </summary>
    public static RegisterState Merge(RegisterState? known, RegisterState arriving)
    {
        if (known is null)
            return arriving;
        var stack = SavedStack.Merge(known.Stack, arriving.Stack);
        return new RegisterState(
            RegisterValue.Merge(known.A, arriving.A),
            RegisterValue.Merge(known.X, arriving.X),
            RegisterValue.Merge(known.Y, arriving.Y),
            RegisterValue.Merge(known.C, arriving.C),
            stack)
        {
            AHigh = RegisterValue.Merge(known.AHigh, arriving.AHigh),
            WhyStack = stack is not null ? null
                : known.Stack is null || arriving.Stack is null ? known.WhyStack ?? arriving.WhyStack
                : Cause.StacksDiffer(depths: true),
        };
    }

    /// <summary>
    /// Returns what the whole of <paramref name="register"/> may hold. For the accumulator, that is
    /// what either of its halves may hold.
    /// </summary>
    public RegisterValue Whole(Registers register) =>
        register == Registers.A ? RegisterValue.Merge(A, AHigh) : Of(register);

    /// <summary>Returns what <paramref name="register"/> may hold.</summary>
    public RegisterValue Of(Registers register) => register switch
    {
        Registers.A => A,
        Registers.X => X,
        Registers.Y => Y,
        _ => C,
    };

    /// <summary>
    /// Returns this state with <paramref name="register"/> holding <paramref name="value"/>. For
    /// the accumulator, both of its halves hold it.
    /// </summary>
    public RegisterState With(Registers register, RegisterValue value) => register switch
    {
        Registers.A => this with { A = value, AHigh = value },
        Registers.X => this with { X = value },
        Registers.Y => this with { Y = value },
        _ => this with { C = value },
    };

    /// <summary>
    /// Returns this state with every register of <paramref name="registers"/> holding
    /// <paramref name="value"/>.
    /// </summary>
    public RegisterState WithEach(Registers registers, RegisterValue value)
    {
        var state = this;
        foreach (var register in RegisterEffects.Each(registers))
            state = state.With(register, value);
        return state;
    }
}
