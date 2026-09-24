using System.Globalization;

namespace Norristown.Semantics;

/// <summary>
/// Represents what the analysis knows about a register it follows by value rather than by
/// width, which is the direct page D or the data bank B. nt65 does not track register values in
/// general, so these become known only through the idioms that load them from constants, a
/// <c>.state</c> or a signature.
/// <para>
/// The default is <see cref="Unchanged"/>, because a routine that declares nothing about D and B
/// is <c>dp*, dbr*</c>.
/// </para>
/// </summary>
/// <param name="Kind">
/// Whether the value is known, one of a set, unknown, or the value the routine was entered with.
/// </param>
/// <param name="Value">The value, when it is known.</param>
public readonly record struct StateValue(StateValueKind Kind, long Value)
{
    /// <summary>
    /// Gets the value meaning the same as when the routine was entered. Where a known value is
    /// needed, this counts as unknown.
    /// </summary>
    public static StateValue Unchanged => default;

    /// <summary>Gets the value meaning not known at this point.</summary>
    public static StateValue Unknown => new(StateValueKind.Unknown, 0);

    /// <summary>
    /// Gets the set of banks the value is one of, when it is <see cref="StateValueKind.Among"/> or
    /// <see cref="StateValueKind.Within"/> a set.
    /// </summary>
    public BankSet Banks { get; init; }

    /// <summary>Gets a value indicating whether the value is known.</summary>
    public bool IsKnown => Kind == StateValueKind.Known;

    /// <summary>
    /// Gets a value indicating whether the value is known to be one of a set of values, including
    /// a set of one.
    /// </summary>
    public bool IsBounded => Kind is StateValueKind.Known or StateValueKind.Among or StateValueKind.Within;

    /// <summary>
    /// Gets a value indicating whether this is the value the routine was entered with, which a
    /// routine that declares it returns unchanged.
    /// </summary>
    public bool IsEntered => Kind is StateValueKind.Unchanged or StateValueKind.Within;

    /// <summary>
    /// Gets the values this may be when it is bounded, which is the one known value or each value
    /// of its set.
    /// </summary>
    public IEnumerable<long> Values => Kind switch
    {
        StateValueKind.Known => [Value],
        StateValueKind.Among or StateValueKind.Within => Banks.Banks,
        _ => [],
    };

    /// <summary>Returns a known value.</summary>
    public static StateValue Of(long value) => new(StateValueKind.Known, value);

    /// <summary>
    /// Returns a value that is one of the banks of <paramref name="banks"/>, which is a known value
    /// when the set holds one bank.
    /// </summary>
    public static StateValue Among(BankSet banks) =>
        banks.Count == 1 ? Of(banks.Banks.First()) : new StateValue(StateValueKind.Among, 0) { Banks = banks };

    /// <summary>
    /// Returns the bank the routine was entered with, which is one of <paramref name="banks"/>. The
    /// result is a known value when the set holds one bank.
    /// </summary>
    public static StateValue Within(BankSet banks) =>
        banks.Count == 1 ? Of(banks.Banks.First()) : new StateValue(StateValueKind.Within, 0) { Banks = banks };

    /// <summary>Returns what two paths arriving at one point agree on.</summary>
    public static StateValue Merge(StateValue a, StateValue b) => a == b ? a : Unknown;

    /// <summary>
    /// Formats a number as messages show it, in at least <paramref name="digits"/> hexadecimal
    /// digits, such as <c>$7e</c> or <c>$2100</c>.
    /// </summary>
    public static string Hex(long value, int digits) =>
        "$" + value.ToString("x" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns a value indicating whether a register holding this value meets what
    /// <paramref name="needed"/> requires. A known requirement needs the same value. A set needs
    /// a value, or a set of values, that all fall within it.
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
    /// Returns what remains after a <c>.state dbr = [...]</c> naming <paramref name="asserted"/>.
    /// The result is the banks both allow, still marked as the entry value if this was one, or
    /// null when they have no bank in common.
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

    /// <summary>
    /// Formats a bounded value for a message, such as <c>$7e</c> or
    /// <c>one of $00-$3f, $80-$bf</c>.
    /// </summary>
    public string Describe(int digits) =>
        Kind is StateValueKind.Among or StateValueKind.Within ? "one of " + Banks.Spell() : Hex(Value, digits);

    /// <summary>
    /// Formats the value as a state item, such as <c>dp = $2100</c>, <c>dbr = [$00..$3f]</c>,
    /// <c>dp?</c> or <c>dp*</c>.
    /// </summary>
    public string Spell(string item) => Kind switch
    {
        StateValueKind.Known => $"{item} = {Hex(Value, item == "dp" ? 4 : 2)}",
        StateValueKind.Among or StateValueKind.Within => $"{item} = {Banks.Write()}",
        StateValueKind.Unknown => item + "?",
        _ => item + "*",
    };
}
