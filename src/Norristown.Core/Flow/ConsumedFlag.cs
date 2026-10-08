using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents an answer of <see cref="FlagExits"/> that a decision or a check in one file
/// depended on: what a call to a routine left one flag as.
/// </summary>
/// <param name="Routine">The routine called.</param>
/// <param name="Flag">The flag the decision or check is about.</param>
/// <param name="Answer">What the answer said about the flag when the file was analyzed.</param>
internal readonly record struct ConsumedFlag(
    Symbol Routine, StatusFlags Flag, (bool Kept, bool? Value, bool Unbacked, bool Quiet) Answer)
{
    /// <summary>Returns whether <paramref name="exits"/> says something else about the flag now.</summary>
    public bool IsStale(FlagExits exits) => exits.Of(Routine).For(Flag) != Answer;
}
