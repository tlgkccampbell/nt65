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

    // The flags in the order the status register holds them, N V D I Z C, which is the order
    // their letters are written in.
    private static readonly StatusFlags[] RegisterOrder =
    [
        StatusFlags.Negative, StatusFlags.Overflow, StatusFlags.Decimal, StatusFlags.InterruptDisable,
        StatusFlags.Zero, StatusFlags.Carry,
    ];

    /// <summary>Gets values that give no flag a value.</summary>
    public static FlagValues None => default;

    /// <summary>
    /// Returns the flags an item's name stands for, which is one flag for one letter and several
    /// for a run of letters such as <c>cz</c>, or <see cref="StatusFlags.None"/> where the name is
    /// not a run of flag letters each written once.
    /// </summary>
    public static StatusFlags Of(string name)
    {
        if (!Syntax.SyntaxFacts.IsFlagRun(name))
            return StatusFlags.None;
        var flags = StatusFlags.None;
        foreach (var letter in name.ToLowerInvariant())
        {
            flags |= letter switch
            {
                'c' => StatusFlags.Carry,
                'z' => StatusFlags.Zero,
                'n' => StatusFlags.Negative,
                'v' => StatusFlags.Overflow,
                'd' => StatusFlags.Decimal,
                _ => StatusFlags.InterruptDisable,
            };
        }
        return flags;
    }

    /// <summary>
    /// Returns the lower-case letters an item writes for <paramref name="flags"/>, in the order the
    /// status register holds them, such as <c>zc</c>.
    /// </summary>
    public static string NameOf(StatusFlags flags) =>
        string.Concat(RegisterOrder.Where(flag => (flags & flag) != 0).Select(flag => flag switch
        {
            StatusFlags.Carry => "c",
            StatusFlags.Zero => "z",
            StatusFlags.Negative => "n",
            StatusFlags.Overflow => "v",
            StatusFlags.Decimal => "d",
            _ => "i",
        }));

    /// <summary>
    /// Returns the item that gives every flag of <paramref name="flags"/> <paramref name="value"/>,
    /// such as <c>c = 0</c> or <c>zc = 0</c>.
    /// </summary>
    public static string Item(StatusFlags flags, bool value) => $"{NameOf(flags)} = {(value ? 1 : 0)}";

    /// <summary>Returns the value given <paramref name="flag"/>, or null where none is given.</summary>
    public bool? ValueOf(StatusFlags flag) => (Known & flag) == 0 ? null : (Set & flag) != 0;

    /// <summary>Returns these values with <paramref name="flag"/> given <paramref name="value"/>.</summary>
    public FlagValues With(StatusFlags flag, bool value) =>
        new(Known | flag, value ? Set | flag : Set & ~flag);

    /// <summary>
    /// Returns the values as items spell them, the flags that are 0 in one item and those that
    /// are 1 in another, such as <c>zc = 0, n = 1</c>.
    /// </summary>
    public override string ToString()
    {
        (StatusFlags Flags, bool Value)[] groups = [(Known & ~Set, false), (Set, true)];
        return string.Join(", ", groups
            .Where(group => group.Flags != StatusFlags.None)
            .Select(group => Item(group.Flags, group.Value)));
    }
}
