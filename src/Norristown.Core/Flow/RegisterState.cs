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
/// <param name="Z">What the zero flag may hold.</param>
/// <param name="N">What the negative flag may hold.</param>
/// <param name="V">What the overflow flag may hold.</param>
/// <param name="Stack">What the routine has pushed, or null when that is not known.</param>
public sealed record RegisterState(
    RegisterValue A, RegisterValue X, RegisterValue Y, RegisterValue C, RegisterValue Z, RegisterValue N, RegisterValue V,
    SavedStack? Stack)
{
    // Every register, one at a time.
    private static readonly Registers[] Every =
        [Registers.A, Registers.X, Registers.Y, Registers.C, Registers.Z, Registers.N, Registers.V];

    /// <summary>
    /// Gets the state of a routine when it is entered, in which each register holds its own entry
    /// value and nothing is pushed.
    /// </summary>
    public static RegisterState Entered { get; } = new(
        RegisterValue.Of(Registers.A), RegisterValue.Of(Registers.X),
        RegisterValue.Of(Registers.Y), RegisterValue.Of(Registers.C), RegisterValue.Of(Registers.Z),
        RegisterValue.Of(Registers.N), RegisterValue.Of(Registers.V), SavedStack.Empty)
    {
        AHigh = RegisterValue.Of(Registers.A),
    };

    /// <summary>
    /// Gets the state where the analysis never saw control arrive, in which nothing is known
    /// about any register or about the stack.
    /// </summary>
    public static RegisterState Unknown { get; } = new(
        RegisterValue.Unknown, RegisterValue.Unknown, RegisterValue.Unknown, RegisterValue.Unknown,
        RegisterValue.Unknown, RegisterValue.Unknown, RegisterValue.Unknown, null)
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

    /// <summary>
    /// Gets the registers that hold exactly their own entry value here without relying on a keep
    /// nobody promised. See <see cref="RoutineRegisters.Backed"/>.
    /// </summary>
    public Registers Backed
    {
        get
        {
            var backed = Registers.None;
            foreach (var register in Every)
            {
                if (Of(register).Backs(register) && (register != Registers.A || AHigh.Backs(register)))
                    backed |= register;
            }
            return backed;
        }
    }

    /// <summary>Gets the registers that hold exactly their own entry value here.</summary>
    public Registers Kept
    {
        get
        {
            var kept = Registers.None;
            foreach (var register in Every)
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
            RegisterValue.Merge(known.Z, arriving.Z),
            RegisterValue.Merge(known.N, arriving.N),
            RegisterValue.Merge(known.V, arriving.V),
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
        Registers.C => C,
        Registers.Z => Z,
        Registers.N => N,
        _ => V,
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
        Registers.C => this with { C = value },
        Registers.Z => this with { Z = value },
        Registers.N => this with { N = value },
        _ => this with { V = value },
    };

    /// <summary>
    /// Returns this state after a routine that keeps <paramref name="registers"/> without
    /// promising to, with what each of them holds marked as held only through that keep.
    /// </summary>
    public RegisterState Unbacking(Registers registers) =>
        registers == Registers.None ? this : Each(registers, value => value.Unbacking());

    /// <summary>
    /// Returns this state with every register of <paramref name="registers"/> holding
    /// <paramref name="value"/>.
    /// </summary>
    public RegisterState WithEach(Registers registers, RegisterValue value) =>
        registers == Registers.None ? this : Each(registers, _ => value);

    /// <summary>
    /// Returns this state with what each register of <paramref name="registers"/> holds changed by
    /// <paramref name="change"/>, as one new state. For the accumulator, both halves are changed.
    /// </summary>
    private RegisterState Each(Registers registers, Func<RegisterValue, RegisterValue> change)
    {
        RegisterValue Changed(Registers register, RegisterValue value) =>
            (registers & register) != Registers.None ? change(value) : value;
        return this with
        {
            A = Changed(Registers.A, A),
            AHigh = Changed(Registers.A, AHigh),
            X = Changed(Registers.X, X),
            Y = Changed(Registers.Y, Y),
            C = Changed(Registers.C, C),
            Z = Changed(Registers.Z, Z),
            N = Changed(Registers.N, N),
            V = Changed(Registers.V, V),
        };
    }
}
