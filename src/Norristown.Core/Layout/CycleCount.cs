using System.Globalization;

namespace Norristown.Layout;

/// <summary>
/// Represents the number of cycles an instruction or a sequence takes, as an interval. Where
/// the count depends on something nt65 cannot know, such as whether an indexed read crosses a
/// page, whether a branch is taken or whether the decimal flag is set, the interval widens
/// instead of guessing.
/// </summary>
/// <param name="Minimum">The fewest cycles it can take.</param>
/// <param name="Maximum">The most cycles it can take.</param>
public readonly record struct CycleCount(int Minimum, int Maximum)
{
    /// <summary>Initializes an exact count of <paramref name="cycles"/> cycles.</summary>
    public CycleCount(int cycles)
        : this(cycles, cycles)
    {
    }

    /// <summary>
    /// Gets a value indicating whether the count is exact, so that it does not depend on anything
    /// decided at run time.
    /// </summary>
    public bool IsExact => Minimum == Maximum;

    /// <summary>Returns the count of <paramref name="first"/> followed by <paramref name="then"/>.</summary>
    public static CycleCount operator +(CycleCount first, CycleCount then) =>
        new(first.Minimum + then.Minimum, first.Maximum + then.Maximum);

    /// <summary>
    /// Returns the count of <paramref name="first"/> followed by <paramref name="then"/>. This is
    /// the named alternative to the <c>+</c> operator.
    /// </summary>
    public static CycleCount Add(CycleCount first, CycleCount then) => first + then;

    /// <summary>Returns this count with <paramref name="cycles"/> more at the top of the interval.</summary>
    public CycleCount Maybe(int cycles) => new(Minimum, Maximum + cycles);

    /// <summary>Formats the interval for display, as <c>4</c> when it is exact and <c>4-5</c> when it is not.</summary>
    public override string ToString() => IsExact
        ? Minimum.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Minimum}-{Maximum}");
}
