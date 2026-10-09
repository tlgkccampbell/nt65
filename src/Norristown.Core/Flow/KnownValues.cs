using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents the flags and the register constants the flag analysis knows at one point. A
/// constant is known where the instructions on every path give the register one, so nothing
/// about memory is assumed.
/// </summary>
/// <param name="Flags">The flags whose value is known, and what each is.</param>
/// <param name="A">The constant the accumulator holds, or null where it is not known.</param>
/// <param name="X">The constant X holds, or null where it is not known.</param>
/// <param name="Y">The constant Y holds, or null where it is not known.</param>
public sealed record KnownValues(FlagValues Flags, long? A, long? X, long? Y);
