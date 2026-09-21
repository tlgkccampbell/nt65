namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// One line of a source file and the run of output lines it became, both counting from zero, as
/// the line map records them. A source line that wrote lines in two places has a run for each.
/// </summary>
/// <param name="Source">The line of the source.</param>
/// <param name="First">The first line of the output it wrote.</param>
/// <param name="Last">The last line of the output it wrote, which may be the first.</param>
internal sealed record OutputRun(int Source, int First, int Last);
