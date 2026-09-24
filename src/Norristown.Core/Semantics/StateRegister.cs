using Norristown.Processor;

namespace Norristown.Semantics;

/// <summary>
/// Represents a 65816 register whose state a <c>.state</c> item declares, with the words that
/// state items and messages use for it. Each register is one of the static instances, so two
/// registers are the same register exactly when they are the same instance.
/// </summary>
public sealed class StateRegister
{
    private StateRegister(string item, string name, bool isPlural, int digits, string? range)
    {
        Item = item;
        Name = name;
        IsPlural = isPlural;
        Digits = digits;
        Range = range;
    }

    /// <summary>Gets the accumulator, whose state is its width.</summary>
    public static StateRegister A { get; } = new("a", "A", isPlural: false, digits: 0, range: null);

    /// <summary>Gets X and Y, whose state is the width they share.</summary>
    public static StateRegister Index { get; } = new("i", "X and Y", isPlural: true, digits: 0, range: null);

    /// <summary>Gets the direct page register D, whose state is a 16-bit address.</summary>
    public static StateRegister DirectPage { get; } =
        new("dp", "D", isPlural: false, digits: 4, range: "the direct page is a 16-bit address");

    /// <summary>Gets the data bank register B, whose state is a bank.</summary>
    public static StateRegister DataBank { get; } =
        new("dbr", "B", isPlural: false, digits: 2, range: "a bank is one byte");

    /// <summary>Gets the word that begins the register's state items, such as <c>a</c> in <c>a16</c>.</summary>
    public string Item { get; }

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

    /// <inheritdoc/>
    public override string ToString() => Name;
}
