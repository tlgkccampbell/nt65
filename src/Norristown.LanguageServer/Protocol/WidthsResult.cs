namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the answer to an <c>nt65/widths</c> request. It lists the runs of lines on which the
/// 65816's register widths and mode are the same. A line in no run has nothing known to draw.
/// </summary>
/// <param name="Runs">Each run, in the order of their first lines. No two runs share a line.</param>
internal sealed record WidthsResult(IReadOnlyList<WidthRun> Runs);
