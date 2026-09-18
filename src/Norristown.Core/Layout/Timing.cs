using System.Collections.Immutable;

namespace Norristown.Layout;

/// <summary>
/// How long an instruction takes, and why that is an interval wherever it is one. An interval
/// on its own leaves a reader to work out which way their line will go, and the answer is
/// always something the processor decides at run time and the program does not say: whether an
/// indexed read crosses a page, whether a branch is taken, how wide a register is. Every term
/// that widens the interval names what would be paid for, in the order it would be paid.
/// </summary>
/// <param name="Count">What it takes.</param>
/// <param name="Causes">What the top of the interval is paid for, in the order it is paid.</param>
public readonly record struct Timing(CycleCount Count, ImmutableArray<string> Causes)
{
    /// <summary>An exact count, which has nothing to explain.</summary>
    public Timing(int cycles)
        : this(new CycleCount(cycles), [])
    {
    }

    /// <summary>A count nothing widens, which likewise has nothing to explain.</summary>
    public Timing(CycleCount count)
        : this(count, [])
    {
    }

    /// <summary>An interval and the one thing it is wide for.</summary>
    public Timing(CycleCount count, string cause)
        : this(count, [cause])
    {
    }

    /// <summary>One count followed by another, and the causes of both in that order.</summary>
    public static Timing operator +(Timing first, Timing then) =>
        new(first.Count + then.Count, first.Causes.AddRange(then.Causes));

    /// <summary>The same, for a caller that would rather not write the operator.</summary>
    public static Timing Add(Timing first, Timing then) => first + then;

    /// <summary>This count with <paramref name="cycles"/> more at the top, paid for <paramref name="cause"/>.</summary>
    public Timing Maybe(int cycles, string cause) => new(Count.Maybe(cycles), Causes.Add(cause));
}
