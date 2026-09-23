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
/// <param name="Kind">Whether the value is known, one of a set, unknown or the one the routine was entered with.</param>
/// <param name="Value">The value, when it is known.</param>
public readonly record struct StateValue(StateValueKind Kind, long Value)
{
    /// <summary>Whatever it was when the routine was entered. Where a known value is needed this counts as unknown.</summary>
    public static StateValue Unchanged => default;

    /// <summary>Not known here.</summary>
    public static StateValue Unknown => new(StateValueKind.Unknown, 0);

    /// <summary>The banks it is one of, when it is <see cref="StateValueKind.Among"/> a set.</summary>
    public BankSet Banks { get; init; }

    /// <summary>Whether the value is known.</summary>
    public bool IsKnown => Kind == StateValueKind.Known;

    /// <summary>Whether it is known to be one of a set of values, a set of one included.</summary>
    public bool IsBounded => Kind is StateValueKind.Known or StateValueKind.Among or StateValueKind.Within;

    /// <summary>Whether it is the value the routine was entered with, which a routine that declares it hands back.</summary>
    public bool IsEntered => Kind is StateValueKind.Unchanged or StateValueKind.Within;

    /// <summary>The values it may be, when it is bounded: the one it is, or each of its set.</summary>
    public IEnumerable<long> Values => Kind switch
    {
        StateValueKind.Known => [Value],
        StateValueKind.Among or StateValueKind.Within => Banks.Banks,
        _ => [],
    };

    /// <summary>A known value.</summary>
    public static StateValue Of(long value) => new(StateValueKind.Known, value);

    /// <summary>One of the banks of <paramref name="banks"/>, which is a known value when the set holds one.</summary>
    public static StateValue Among(BankSet banks) =>
        banks.Count == 1 ? Of(banks.Banks.First()) : new StateValue(StateValueKind.Among, 0) { Banks = banks };

    /// <summary>The bank the routine was entered with, one of <paramref name="banks"/>; a known value when the set holds one.</summary>
    public static StateValue Within(BankSet banks) =>
        banks.Count == 1 ? Of(banks.Banks.First()) : new StateValue(StateValueKind.Within, 0) { Banks = banks };

    /// <summary>What two paths arriving at one place agree on.</summary>
    public static StateValue Merge(StateValue a, StateValue b) => a == b ? a : Unknown;

    /// <summary>A number as the messages write it, in at least <paramref name="digits"/> hexadecimal digits: <c>$7e</c>, <c>$2100</c>.</summary>
    public static string Hex(long value, int digits) =>
        "$" + value.ToString("x" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a register holding this meets what <paramref name="needed"/> says of it: the same
    /// value, or for a set, a value or a set of values that are all among it.
    /// </summary>
    public bool Meets(StateValue needed) => needed.Kind switch
    {
        StateValueKind.Known => this == needed,
        StateValueKind.Among or StateValueKind.Within => Kind switch
        {
            StateValueKind.Known => needed.Banks.Contains(Value),
            StateValueKind.Among or StateValueKind.Within => Banks.IsSubsetOf(needed.Banks),
            _ => false,
        },
        _ => true,
    };

    /// <summary>
    /// What a <c>.state dbr = [...]</c> naming <paramref name="asserted"/> leaves: the banks both
    /// say it may be, still marked as the entry value if it was one, or null when they have none
    /// in common.
    /// </summary>
    public StateValue? Narrowed(BankSet asserted)
    {
        switch (Kind)
        {
            case StateValueKind.Known:
                return asserted.Contains(Value) ? this : null;
            case StateValueKind.Among or StateValueKind.Within:
                var common = Banks.Intersect(asserted);
                if (common.Count == 0)
                    return null;
                return Kind == StateValueKind.Within ? Within(common) : Among(common);
            default:
                return Among(asserted);
        }
    }

    /// <summary>What a message says a bounded value is: <c>$7e</c>, or <c>one of $00-$3f, $80-$bf</c>.</summary>
    public string Describe(int digits) =>
        Kind is StateValueKind.Among or StateValueKind.Within ? "one of " + Banks.Spell() : Hex(Value, digits);

    /// <summary>The value as an item names it: <c>dp = $2100</c>, <c>dbr = [$00..$3f]</c>, <c>dp?</c> or <c>dp*</c>.</summary>
    public string Spell(string item) => Kind switch
    {
        StateValueKind.Known => $"{item} = {Hex(Value, item == "dp" ? 4 : 2)}",
        StateValueKind.Among or StateValueKind.Within => $"{item} = {Banks.Write()}",
        StateValueKind.Unknown => item + "?",
        _ => item + "*",
    };
}
