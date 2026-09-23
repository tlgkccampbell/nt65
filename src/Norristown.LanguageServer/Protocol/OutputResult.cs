namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// The output for one source file: the ca65 a build would write for it from the program as it
/// currently stands in the editor, and which output lines each source line produced.
/// </summary>
/// <param name="Uri">The source file, as the client names it.</param>
/// <param name="Path">Where the output goes, relative to the project root; also the view's title.</param>
/// <param name="Version">The revision of the source this was written from, or null for a file nobody has open.</param>
/// <param name="Text">The ca65, with <c>\n</c> line endings.</param>
/// <param name="Lines">Each source line and the run of output lines it wrote, in output order.</param>
/// <param name="Header">
/// How many lines the output starts with that no source line produced, such as the
/// <c>.feature</c> block, which are there for ca65 rather than for a reader. It is therefore
/// also the index of the first line a reader cares about, and a view opens there when the
/// caret maps to no output line.
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
