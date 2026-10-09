namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the data of the map that lies on no page, in one segment or at addresses the source fixes.</summary>
/// <param name="Id">
/// The group's name for the client, which is the segment's name, or <c>fixed</c> or
/// <c>hardware</c> for the locations at addresses the source fixes.
/// </param>
/// <param name="Name">The segment's name, or null for the locations at addresses the source fixes.</param>
/// <param name="Hardware">Whether the locations are hardware registers.</param>
/// <param name="Relation">The strongest way any of its locations is shared.</param>
/// <param name="Hazard">Whether a use of any of its locations is a hazard.</param>
/// <param name="Locations">The locations, by address where the addresses are known, and otherwise as declared.</param>
internal sealed record DirectPageSegment(
    string Id, string? Name, bool Hardware, string Relation, bool Hazard, IReadOnlyList<DirectPageLocation> Locations);
