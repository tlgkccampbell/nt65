namespace Norristown.Flow;

/// <summary>
/// Represents a run of addresses that two locations both take. The other location is on another
/// <see cref="DirectPage"/>, or on the same page when <paramref name="Kind"/> says so.
/// </summary>
/// <param name="Here">The name of the location on the page that reports the run, as <see cref="PageLocation.Name"/> gives it.</param>
/// <param name="There">The name of the other location.</param>
/// <param name="Page">The other location's page, which is the reporting page itself for a run on one page.</param>
/// <param name="First">The first address both take.</param>
/// <param name="Last">The last address both take.</param>
/// <param name="Kind">How the two locations come to take the same bytes.</param>
public sealed record SharedBytes(string Here, string There, DirectPage Page, long First, long Last, SharedBytesKind Kind);
