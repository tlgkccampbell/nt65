namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// What one source file became: the ca65 a build writes for it, as the program stands in the
/// editor now, and which lines of it each line of the source wrote.
/// </summary>
/// <param name="Uri">The source file, as the client names it.</param>
/// <param name="Path">Where the output goes, relative to the project root, which names the view.</param>
/// <param name="Version">The revision of the source this was written from, or null for a file nobody has open.</param>
/// <param name="Text">The ca65, with <c>\n</c> line endings.</param>
/// <param name="Lines">Each source line and the run of output lines it wrote, in output order.</param>
/// <param name="Header">
/// The first line of the output a reader has no use for: the <c>.feature</c> block is there for
/// ca65. A view opens past it where the caret maps nowhere.
/// </param>
/// <param name="Note">Why the output is incomplete, or null when the program is not wrong.</param>
internal sealed record OutputResult(
    string Uri,
    string Path,
    int? Version,
    string Text,
    IReadOnlyList<OutputRun> Lines,
    int Header,
    string? Note);
