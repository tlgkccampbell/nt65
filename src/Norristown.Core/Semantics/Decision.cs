namespace Norristown.Semantics;

/// <summary>
/// Represents what the configuration alone makes of a value: the value it decides, or the reason
/// it does not decide one.
/// </summary>
/// <param name="Value">The value, which is unknown when <paramref name="Why"/> is given.</param>
/// <param name="Why">The reason the configuration does not decide the value, or null when it does.</param>
internal readonly record struct Decision(Value Value, Undecided? Why)
{
    /// <summary>Returns a decision for <paramref name="value"/>.</summary>
    public static Decision Of(Value value) => new(value, null);

    /// <summary>Returns a decision that the configuration does not decide, because of <paramref name="why"/>.</summary>
    public static Decision Not(Undecided why) => new(Value.Unknown, why);
}
