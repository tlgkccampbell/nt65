using Norristown.Processor;

namespace Norristown.Semantics;

/// <summary>
/// Represents the values a signature gives some of the status flags, such as the
/// <c>c = 0, d = 0</c> a routine needs on entry or the <c>z = 1</c> it returns with. A flag
/// the signature does not name is not in <see cref="Known"/>.
/// </summary>
/// <param name="Known">The flags given a value.</param>
/// <param name="Set">The flags among <paramref name="Known"/> given the value 1.</param>
public readonly record struct FlagValues(StatusFlags Known, StatusFlags Set)
{
    /// <summary>Gets the flags an item may give a value, in the order they are listed.</summary>
    public static IReadOnlyList<StatusFlags> Named { get; } =
    [
        StatusFlags.Carry, StatusFlags.Zero, StatusFlags.Negative, StatusFlags.Overflow,
        StatusFlags.Decimal, StatusFlags.InterruptDisable,
    ];

    /// <summary>Gets values that give no flag a value.</summary>
    public static FlagValues None => default;

    /// <summary>Returns the flag an item's name stands for, or <see cref="StatusFlags.None"/>.</summary>
    public static StatusFlags Of(string name) => name.ToLowerInvariant() switch
    {
        "c" => StatusFlags.Carry,
        "z" => StatusFlags.Zero,
        "n" => StatusFlags.Negative,
        "v" => StatusFlags.Overflow,
        "d" => StatusFlags.Decimal,
        "i" => StatusFlags.InterruptDisable,
        _ => StatusFlags.None,
    };

    /// <summary>Returns the lower-case name an item gives <paramref name="flag"/>, such as <c>c</c>.</summary>
    public static string NameOf(StatusFlags flag) => flag switch
    {
        StatusFlags.Carry => "c",
        StatusFlags.Zero => "z",
        StatusFlags.Negative => "n",
        StatusFlags.Overflow => "v",
        StatusFlags.Decimal => "d",
        StatusFlags.InterruptDisable => "i",
        _ => flag.ToString().ToLowerInvariant(),
    };

    /// <summary>Returns the item that gives <paramref name="flag"/> <paramref name="value"/>, such as <c>c = 0</c>.</summary>
    public static string Item(StatusFlags flag, bool value) => $"{NameOf(flag)} = {(value ? 1 : 0)}";

    /// <summary>Returns the value given <paramref name="flag"/>, or null where none is given.</summary>
    public bool? ValueOf(StatusFlags flag) => (Known & flag) == 0 ? null : (Set & flag) != 0;

    /// <summary>Returns these values with <paramref name="flag"/> given <paramref name="value"/>.</summary>
    public FlagValues With(StatusFlags flag, bool value) =>
        new(Known | flag, value ? Set | flag : Set & ~flag);

    /// <summary>Returns each value as an item spells it, such as <c>c = 0, z = 1</c>.</summary>
    public override string ToString()
    {
        var (known, set) = (Known, Set);
        return string.Join(", ", Named.Where(flag => (known & flag) != 0).Select(flag => Item(flag, (set & flag) != 0)));
    }
}
