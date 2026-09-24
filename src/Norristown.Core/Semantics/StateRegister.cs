using System.Globalization;
using Norristown.Processor;

namespace Norristown.Semantics;

/// <summary>
/// Represents a 65816 register whose state a <c>.state</c> item declares, with the words that
/// state items, segment attributes and messages use for it. Each register is one of the static instances, so two
/// registers are the same register exactly when they are the same instance.
/// </summary>
public sealed class StateRegister
{
    private StateRegister(
        StatePart part, string item, string? attribute, string name, bool isPlural, int digits, string? range)
    {
        Part = part;
        Item = item;
        Attribute = attribute;
        Name = name;
        IsPlural = isPlural;
        Digits = digits;
        Range = range;
    }

    /// <summary>Gets the accumulator, whose state is its width.</summary>
    public static StateRegister A { get; } =
        new(StatePart.A, "a", attribute: null, "A", isPlural: false, digits: 0, range: null);

    /// <summary>Gets X and Y, whose state is the width they share.</summary>
    public static StateRegister Index { get; } =
        new(StatePart.Index, "i", attribute: null, "X and Y", isPlural: true, digits: 0, range: null);

    /// <summary>Gets the direct page register D, whose state is a 16-bit address.</summary>
    public static StateRegister DirectPage { get; } =
        new(StatePart.DirectPage, "dp", "dp", "D", isPlural: false, digits: 4, range: "the direct page is a 16-bit address");

    /// <summary>Gets the data bank register B, whose state is a bank.</summary>
    public static StateRegister DataBank { get; } =
        new(StatePart.DataBank, "dbr", "bank", "B", isPlural: false, digits: 2, range: "a bank is one byte");

    /// <summary>Gets every register, in the order a <c>.state</c> item lists them.</summary>
    public static IReadOnlyList<StateRegister> All { get; } = [A, Index, DirectPage, DataBank];

    /// <summary>Gets the part of the state that the register's items declare.</summary>
    public StatePart Part { get; }

    /// <summary>Gets the word that begins the register's state items, such as <c>a</c> in <c>a16</c>.</summary>
    public string Item { get; }

    /// <summary>
    /// Gets the word a segment's attribute uses for the register's value, such as <c>bank</c>,
    /// or null for a register that no segment sets.
    /// </summary>
    public string? Attribute { get; }

    /// <summary>Gets the register's name as a message gives it, such as <c>X and Y</c>.</summary>
    public string Name { get; }

    /// <summary>Gets whether <see cref="Name"/> names more than one register, and so takes a plural verb.</summary>
    public bool IsPlural { get; }

    /// <summary>Gets the verb a message uses to say what the register is: <c>is</c> or <c>are</c>.</summary>
    public string Is => IsPlural ? "are" : "is";

    /// <summary>
    /// Gets the number of hex digits the register's value is written with, or zero for a
    /// register whose state is a width.
    /// </summary>
    public int Digits { get; }

    /// <summary>Gets the largest value the register holds, or zero for a register whose state is a width.</summary>
    public int Maximum => Digits == 0 ? 0 : (1 << (4 * Digits)) - 1;

    /// <summary>
    /// Gets why a value above <see cref="Maximum"/> does not fit, as a message says it, or null for
    /// a register whose state is a width.
    /// </summary>
    public string? Range { get; }

    /// <summary>Returns the register whose width sizes an immediate.</summary>
    public static StateRegister Of(WidthRegister register) => register == WidthRegister.A ? A : Index;

    /// <summary>
    /// Returns the register that a state item's word names, in any letter case, or null for a
    /// word that names none. A register whose state is a width is named by its item alone, such
    /// as <c>a</c>, or with the width, such as <c>a16</c>.
    /// </summary>
    public static StateRegister? FromItem(string word) => All.FirstOrDefault(register =>
        word.Equals(register.Item, StringComparison.OrdinalIgnoreCase)
        || (register.Digits == 0 && (word.Equals(register.WidthItem(8), StringComparison.OrdinalIgnoreCase)
            || word.Equals(register.WidthItem(16), StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Returns the register that a segment's attribute sets, in any letter case, or null for an
    /// attribute that sets none.
    /// </summary>
    public static StateRegister? FromAttribute(string word) =>
        All.FirstOrDefault(register => word.Equals(register.Attribute, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the item that declares the register <paramref name="bits"/> wide, such as
    /// <c>a16</c>. A width directive is spelled the same after a <c>.</c>.
    /// </summary>
    public string WidthItem(int bits) => $"{Item}{bits}";

    /// <summary>
    /// Returns the range of values the register holds as a message gives it, such as
    /// <c>$0000 to $ffff</c>, for a register whose state is a value.
    /// </summary>
    public string ValueRange()
    {
        var format = $"x{Digits}";
        return $"${0.ToString(format, CultureInfo.InvariantCulture)} to ${Maximum.ToString(format, CultureInfo.InvariantCulture)}";
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
