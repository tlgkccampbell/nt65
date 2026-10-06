namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the addresses that two pages both cover.</summary>
/// <param name="Page">The other page's name.</param>
/// <param name="First">The first address both cover.</param>
/// <param name="Last">The last address both cover.</param>
/// <param name="Shared">The bytes that a location of each page both take, or none when the pages only overlap.</param>
internal sealed record DirectPageOverlap(string Page, long First, long Last, IReadOnlyList<DirectPageShared> Shared);
