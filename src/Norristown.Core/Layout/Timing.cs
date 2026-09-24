using System.Collections.Immutable;

namespace Norristown.Layout;

/// <summary>
/// Represents how long an instruction takes and, where that is an interval, why. An interval on
/// its own leaves the reader to work out which end their line will hit. The answer always depends
/// on something decided at run time that the program text does not say, such as whether an
/// indexed read crosses a page, whether a branch is taken or how wide a register is. Each cause
/// that widens the interval names what the extra cycles are spent on, in the order they are spent.
/// </summary>
/// <param name="Count">The cycle count.</param>
/// <param name="Causes">What the extra cycles at the top of the interval are spent on, in order.</param>
public readonly record struct Timing(CycleCount Count, ImmutableArray<string> Causes)
{
    /// <summary>Initializes an exact count, which has no causes to explain.</summary>
    public Timing(int cycles)
        : this(new CycleCount(cycles), [])
    {
    }

    /// <summary>Initializes a count with no causes recorded for it.</summary>
    public Timing(CycleCount count)
        : this(count, [])
    {
    }

    /// <summary>Initializes an interval with the single cause that widens it.</summary>
    public Timing(CycleCount count, string cause)
        : this(count, [cause])
    {
    }

    /// <summary>
    /// Returns the timing of <paramref name="first"/> followed by <paramref name="then"/>, with the
    /// causes of both in that order.
    /// </summary>
    public static Timing operator +(Timing first, Timing then) =>
        new(first.Count + then.Count, first.Causes.AddRange(then.Causes));

    /// <summary>
    /// Returns the timing of <paramref name="first"/> followed by <paramref name="then"/>, with the
    /// causes of both in that order. This is the named alternative to the <c>+</c> operator.
    /// </summary>
    public static Timing Add(Timing first, Timing then) => first + then;

    /// <summary>
    /// Returns this timing with <paramref name="cycles"/> more at the top of the interval, spent on
    /// <paramref name="cause"/>.
    /// </summary>
    public Timing Maybe(int cycles, string cause) => new(Count.Maybe(cycles), Causes.Add(cause));
}
