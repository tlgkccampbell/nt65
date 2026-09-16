using System.Globalization;

namespace Norristown.Semantics;

/// <summary>
/// What the analysis knows about a register it follows by value rather than by width: the
/// direct page D or the data bank B. nt65 does not track register values in general, so
/// these become known only through the idioms that load them from constants, a <c>.state</c>
/// or a signature.
/// <para>
/// The default is <see cref="Unchanged"/>, because a routine that says nothing about D and B
/// is <c>dp*, dbr*</c>.
/// </para>
/// </summary>
/// <param name="Kind">Whether the value is known, unknown or the one the routine was entered with.</param>
/// <param name="Value">The value, when it is known.</param>
public readonly record struct StateValue(StateValueKind Kind, long Value)
{
    /// <summary>Whatever it was when the routine was entered. Where a known value is needed this counts as unknown.</summary>
    public static StateValue Unchanged => default;

    /// <summary>Not known here.</summary>
    public static StateValue Unknown => new(StateValueKind.Unknown, 0);

    /// <summary>Whether the value is known.</summary>
    public bool IsKnown => Kind == StateValueKind.Known;

    /// <summary>A known value.</summary>
    public static StateValue Of(long value) => new(StateValueKind.Known, value);

    /// <summary>What two paths arriving at one place agree on.</summary>
    public static StateValue Merge(StateValue a, StateValue b) => a == b ? a : Unknown;

    /// <summary>A number as the messages write it, in at least <paramref name="digits"/> hexadecimal digits: <c>$7e</c>, <c>$2100</c>.</summary>
    public static string Hex(long value, int digits) =>
        "$" + value.ToString("x" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>The value as an item names it: <c>dp = $2100</c>, <c>dp?</c> or <c>dp*</c>.</summary>
    public string Spell(string item) => Kind switch
    {
        StateValueKind.Known => $"{item} = {Hex(Value, item == "dp" ? 4 : 2)}",
        StateValueKind.Unknown => item + "?",
        _ => item + "*",
    };
}
