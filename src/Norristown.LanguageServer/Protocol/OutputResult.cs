namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the output for one source file. It holds the ca65 source a build would write for the
/// file from the program as it currently stands in the editor, and records which output lines
/// each source line produced.
/// </summary>
/// <param name="Uri">The source file's URI, as the client names it.</param>
/// <param name="Path">
/// The output file's path relative to the project root, which is also the view's title.
/// </param>
/// <param name="Version">
/// The version of the source the output was produced from, or null for a file that no client has
/// open.
/// </param>
/// <param name="Text">The ca65 source, with <c>\n</c> line endings.</param>
/// <param name="Lines">Each source line and the run of output lines it produced, in output order.</param>
/// <param name="Header">
/// The number of lines at the start of the output that no source line produced, such as the
/// <c>.feature</c> block, which are there for ca65 rather than for a reader. It is therefore also
/// the index of the first line a reader cares about, and a view opens there when the caret maps to
/// no output line.
/// </param>
/// <param name="Note">Why the output is incomplete, or null when the program has no errors.</param>
internal sealed record OutputResult(
    string Uri,
    string Path,
    int? Version,
    string Text,
    IReadOnlyList<OutputRun> Lines,
    int Header,
    string? Note);
