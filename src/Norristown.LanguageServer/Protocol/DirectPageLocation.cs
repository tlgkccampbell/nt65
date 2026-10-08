namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one location on a page, with the routines that use it.</summary>
/// <param name="Name">The location's name, or for a constant address without a symbol the address, such as <c>$00FB</c>.</param>
/// <param name="Declaration">Where its name is declared, or null for a constant address without a symbol.</param>
/// <param name="Offset">The offset from D to its first byte, or null when it is not known.</param>
/// <param name="Size">The number of bytes it takes, or null when that is not known.</param>
/// <param name="Address">Its first address, or null when that is not known.</param>
/// <param name="Layout">
/// Where its address comes from. It is <c>fixed</c> when the source fixes it, <c>built</c> when the
/// last build gives it, <c>configured</c> when a linked config predicts it, and <c>guessed</c> when
/// it is guessed from the page's base.
/// </param>
/// <param name="Type">The element it is declared with, such as <c>.word</c>, or an empty string.</param>
/// <param name="Relation">How the routines that use it share it.</param>
/// <param name="Hazard">Whether a routine's use of it is a hazard.</param>
/// <param name="Shared">The bytes it shares with other locations, on other pages or on its own.</param>
/// <param name="References">
/// The lines that take its address without reaching it, such as <c>ldx #tmp</c> or <c>.addr tmp</c>.
/// A location with none of its own accesses but some of these is used through a pointer or an index.
/// </param>
/// <param name="Accesses">The number of instructions that reach it.</param>
/// <param name="PerPass">
/// How many times those instructions run in one pass through each outermost caller, added up. It
/// counts only the loops whose counts are known, through the calls as well as in each routine.
/// </param>
/// <param name="Uncounted">
/// The number of those instructions that are in a loop whose count is not known, or that are
/// reached through a call in one, so that they may run more often than <paramref name="PerPass"/> says.
/// </param>
/// <param name="Routines">The call trees from the outermost callers down to each routine that reaches it.</param>
internal sealed record DirectPageLocation(
    string Name, Location? Declaration, long? Offset, long? Size, long? Address, string Layout, string Type, string Relation, bool Hazard,
    IReadOnlyList<DirectPageShared> Shared, IReadOnlyList<Location> References, int Accesses, long PerPass, int Uncounted, IReadOnlyList<DirectPageRoutine> Routines);
