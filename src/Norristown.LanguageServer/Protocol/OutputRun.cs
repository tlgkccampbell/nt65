namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents one line of a source file and the run of output lines it produced, both zero-based,
/// as the line map records them. A source line that produced lines in two places has a run for
/// each.
/// </summary>
/// <param name="Source">The source line.</param>
/// <param name="First">The first output line the source line produced.</param>
/// <param name="Last">The last output line the source line produced, which may equal the first.</param>
internal sealed record OutputRun(int Source, int First, int Last);
