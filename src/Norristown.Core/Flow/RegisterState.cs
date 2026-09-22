using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// What each register may hold at one point in a routine, and what the routine has pushed.
/// </summary>
/// <param name="A">What the accumulator may hold.</param>
/// <param name="X">What X may hold.</param>
/// <param name="Y">What Y may hold.</param>
/// <param name="C">What the carry may hold.</param>
/// <param name="Stack">What the routine has pushed, or null when that is not known.</param>
public sealed record RegisterState(
    RegisterValue A, RegisterValue X, RegisterValue Y, RegisterValue C, SavedStack? Stack)
{
    /// <summary>What a routine holds when it is entered: each register its own entry value, nothing pushed.</summary>
    public static RegisterState Entered { get; } = new(
        RegisterValue.Of(Registers.A), RegisterValue.Of(Registers.X),
        RegisterValue.Of(Registers.Y), RegisterValue.Of(Registers.C), SavedStack.Empty);

    /// <summary>
    /// What is held where the analysis never saw control arrive: nothing known, of any
    /// register or of the stack.
    /// </summary>
    public static RegisterState Unknown { get; } = new(
        RegisterValue.Unknown, RegisterValue.Unknown, RegisterValue.Unknown, RegisterValue.Unknown, null);

    /// <summary>
    /// What a label another routine may jump into starts from: nothing known in the registers,
    /// over the stack a call to the routine leaves, which is nothing, since whoever jumps in
    /// arrives as a call would and has made no save of this routine's.
    /// </summary>
    public static RegisterState Outside { get; } = Unknown with { Stack = SavedStack.Empty };

    /// <summary>Why the stack is unknown, where it is and the analysis can say.</summary>
    public Cause? WhyStack { get; init; }

    /// <summary>What <paramref name="register"/> may hold.</summary>
    public RegisterValue Of(Registers register) => register switch
    {
        Registers.A => A,
        Registers.X => X,
        Registers.Y => Y,
        _ => C,
    };

    /// <summary>The same state with <paramref name="register"/> holding <paramref name="value"/>.</summary>
    public RegisterState With(Registers register, RegisterValue value) => register switch
    {
        Registers.A => this with { A = value },
        Registers.X => this with { X = value },
        Registers.Y => this with { Y = value },
        _ => this with { C = value },
    };

    /// <summary>The same state with every register of <paramref name="registers"/> holding <paramref name="value"/>.</summary>
    public RegisterState WithEach(Registers registers, RegisterValue value)
    {
        var state = this;
        foreach (var register in RegisterEffects.Each(registers))
            state = state.With(register, value);
        return state;
    }

    /// <summary>The registers that hold exactly their own entry value here.</summary>
    public Registers Kept
    {
        get
        {
            var kept = Registers.None;
            foreach (var register in RegisterEffects.Each(Registers.All))
            {
                if (Of(register).Holds(register))
                    kept |= register;
            }
            return kept;
        }
    }

    /// <summary>What two paths arriving at one place agree on: what either of them may have left.</summary>
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
            WhyStack = stack is null ? known.WhyStack ?? arriving.WhyStack : null,
        };
    }
}
