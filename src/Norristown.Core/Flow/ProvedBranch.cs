namespace Norristown.Flow;

/// <summary>
/// Represents a conditional branch whose outcome the <see cref="FlagAnalysis"/> proves, because
/// the flag it tests has the same value on every path to it.
/// </summary>
/// <param name="Flag">The flag the branch tests.</param>
/// <param name="Value">The value the flag has on every path to the branch.</param>
/// <param name="Taken">Whether the branch is always taken, as opposed to never taken.</param>
internal readonly record struct ProvedBranch(Processor.StatusFlags Flag, bool Value, bool Taken)
{
    /// <summary>Gets the reason the branch goes one way only, in the words a message uses.</summary>
    public string Why => FlagState.Describe(Flag, Value);
}
