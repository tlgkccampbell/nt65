namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one location on a page, with the routines that use it.</summary>
/// <param name="Name">The location's name.</param>
/// <param name="Declaration">Where its name is declared.</param>
/// <param name="Offset">The offset from D to its first byte, or null when it is not known.</param>
/// <param name="Size">The number of bytes it takes, or null when that is not known.</param>
/// <param name="Address">Its first address, or null when that is not known.</param>
/// <param name="Fixed">Whether the source fixes its address, so that it is not a prediction.</param>
/// <param name="Type">The element it is declared with, such as <c>.word</c>, or an empty string.</param>
/// <param name="Relation">How the routines that use it share it.</param>
/// <param name="Hazard">Whether a routine's use of it is a hazard.</param>
/// <param name="Shared">The bytes it shares with locations on other pages.</param>
/// <param name="Accesses">The number of instructions that reach it.</param>
/// <param name="Loops">The number of those instructions that are inside a loop.</param>
/// <param name="Routines">The call trees from the outermost callers down to each routine that reaches it.</param>
internal sealed record DirectPageLocation(
    string Name, Location Declaration, long? Offset, long? Size, long? Address, bool Fixed, string Type, string Relation, bool Hazard,
    IReadOnlyList<DirectPageShared> Shared, int Accesses, int Loops, IReadOnlyList<DirectPageRoutine> Routines);
