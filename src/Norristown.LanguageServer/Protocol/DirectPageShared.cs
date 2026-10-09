namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a run of addresses that two locations both take, on two pages or on one.</summary>
/// <param name="Here">The location on the page that reports the run.</param>
/// <param name="There">The other location.</param>
/// <param name="Page">The other location's page, which is the reporting page itself for a run on one page.</param>
/// <param name="First">The first address both take.</param>
/// <param name="Last">The last address both take.</param>
/// <param name="Kind">
/// How the two come to take the same bytes. It is <c>page</c> for locations on two pages, and
/// <c>deliberate</c> for two addresses on one page that the source fixes. It is <c>authored</c>
/// for two on one page that the linked configuration places so, and <c>collision</c> for two on
/// one page that neither the source nor the configuration places both there. It is
/// <c>unverified</c> for an address the source fixes among a segment's bytes whose addresses are
/// only predicted, so that the two may or may not take the same bytes.
/// </param>
internal sealed record DirectPageShared(string Here, string There, string Page, long First, long Last, string Kind);
