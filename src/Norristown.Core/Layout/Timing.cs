using System.Collections.Immutable;

namespace Norristown.Layout;

/// <summary>
/// How long an instruction takes, and, where that is an interval, why. An interval on its own
/// leaves the reader to work out which end their line will hit, and the answer always depends
/// on something decided at run time that the program text does not say: whether an indexed
/// read crosses a page, whether a branch is taken, how wide a register is. Each cause that
/// widens the interval names what the extra cycles are spent on, in the order they are spent.
/// </summary>
/// <param name="Count">The cycle count.</param>
/// <param name="Causes">What the extra cycles at the top of the interval are spent on, in order.</param>
public readonly record struct Timing(CycleCount Count, ImmutableArray<string> Causes)
{
    /// <summary>An exact count, which has nothing to explain.</summary>
    public Timing(int cycles)
        : this(new CycleCount(cycles), [])
    {
    }

    /// <summary>A count with no causes recorded for it.</summary>
    public Timing(CycleCount count)
        : this(count, [])
    {
    }

    /// <summary>An interval and the single cause that widens it.</summary>
    public Timing(CycleCount count, string cause)
        : this(count, [cause])
    {
    }

    /// <summary>One count followed by another, and the causes of both in that order.</summary>
    public static Timing operator +(Timing first, Timing then) =>
        new(first.Count + then.Count, first.Causes.AddRange(then.Causes));

    /// <summary>The same, for a caller that would rather not write the operator.</summary>
    public static Timing Add(Timing first, Timing then) => first + then;

    /// <summary>This count with <paramref name="cycles"/> more at the top, spent on <paramref name="cause"/>.</summary>
    public Timing Maybe(int cycles, string cause) => new(Count.Maybe(cycles), Causes.Add(cause));
}
