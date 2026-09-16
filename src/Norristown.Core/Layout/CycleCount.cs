using System.Globalization;

namespace Norristown.Layout;

/// <summary>
/// How many cycles something takes, as an interval. Where the count depends on
/// something nt65 cannot know — whether an indexed read crosses a page, whether a branch
/// is taken, whether the decimal flag is set — the interval widens instead of guessing.
/// </summary>
/// <param name="Least">The fewest cycles it can take.</param>
/// <param name="Most">The most.</param>
public readonly record struct CycleCount(int Least, int Most)
{
    /// <summary>An exact count.</summary>
    public CycleCount(int cycles)
        : this(cycles, cycles)
    {
    }

    /// <summary>Whether the count is the same however the instruction goes.</summary>
    public bool IsExact => Least == Most;

    /// <summary>One count followed by another.</summary>
    public static CycleCount operator +(CycleCount first, CycleCount then) =>
        new(first.Least + then.Least, first.Most + then.Most);

    /// <summary>The same, for a caller that would rather not write the operator.</summary>
    public static CycleCount Add(CycleCount first, CycleCount then) => first + then;

    /// <summary>This count with <paramref name="cycles"/> more at the top of the interval.</summary>
    public CycleCount Maybe(int cycles) => new(Least, Most + cycles);

    /// <summary>The interval as it is shown: <c>4</c> when it is exact, <c>4-5</c> when it is not.</summary>
    public override string ToString() => IsExact
        ? Least.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Least}-{Most}");
}
