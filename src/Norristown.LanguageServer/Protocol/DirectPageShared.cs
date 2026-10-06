namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a run of addresses that a location on one page and a location on another both take.</summary>
/// <param name="Here">The location on the page that reports the run.</param>
/// <param name="There">The location on the other page.</param>
/// <param name="Page">The other page's name.</param>
/// <param name="First">The first address both take.</param>
/// <param name="Last">The last address both take.</param>
internal sealed record DirectPageShared(string Here, string There, string Page, long First, long Last);
