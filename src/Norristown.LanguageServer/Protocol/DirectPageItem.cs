namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one direct page of the map.</summary>
/// <param name="Id">The page's name, such as <c>$0080</c>, or <c>?</c> for the page whose D is not known.</param>
/// <param name="Base">The value of D, or null for the page whose D is not known.</param>
/// <param name="Segments">The segments whose symbols are reached through the page.</param>
/// <param name="Hardware">Whether every location on the page is a hardware register.</param>
/// <param name="Relation">
/// The strongest way any of its locations is shared, or <c>unknown</c> for the page whose D is not
/// known.
/// </param>
/// <param name="Hazard">Whether a use of any location on the page is a hazard.</param>
/// <param name="Used">The number of bytes its locations take, where their offsets are known.</param>
/// <param name="Direct">The number of instructions that reach a location through the page with direct addressing.</param>
/// <param name="Overlaps">The other pages that cover some of the same addresses.</param>
/// <param name="Locations">The locations on the page, by offset.</param>
/// <param name="Notes">The facts about the page's layout that are about no one line, such as a segment outside the page.</param>
/// <param name="Groups">
/// The routines that reach memory while D is not known, grouped by why, on the page whose D is not
/// known.
/// </param>
internal sealed record DirectPageItem(
    string Id, long? Base, IReadOnlyList<string> Segments, bool Hardware, string Relation, bool Hazard, long Used, int Direct,
    IReadOnlyList<DirectPageOverlap> Overlaps, IReadOnlyList<DirectPageLocation> Locations, IReadOnlyList<DirectPageNote> Notes,
    IReadOnlyList<DirectPageGroup> Groups);
