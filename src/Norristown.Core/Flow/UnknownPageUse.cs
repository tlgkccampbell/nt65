using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>Represents a routine that reaches a location through the direct page while D is not known.</summary>
/// <param name="Location">The symbol the operand names.</param>
/// <param name="Home">The page the location lives on, or null when it lives on none.</param>
/// <param name="Offset">The location's offset on its own page, or null when it is not known.</param>
/// <param name="Use">What the routine does with the location there.</param>
/// <param name="Reason">Why D is not known.</param>
public sealed record UnknownPageUse(Symbol Location, DirectPage? Home, long? Offset, PageUse Use, UnknownPageReason Reason);
