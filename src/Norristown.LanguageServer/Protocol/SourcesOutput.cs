namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one output of the instruction at the caret, with the places that read its value.</summary>
/// <param name="Name">
/// The output's name: <c>A</c>, <c>X</c>, <c>Y</c>, <c>C</c>, <c>N</c>, <c>Z</c> or <c>V</c>; a width
/// as a signature spells the one a routine called needs, such as <c>a16</c>; or a symbol's name for a
/// location in memory.
/// </param>
/// <param name="Group">The root symbol a memory output is grouped under, or null.</param>
/// <param name="Category">The output's kind: <c>register</c>, <c>flag</c>, <c>width</c> or <c>memory</c>.</param>
/// <param name="Readers">Each place that reads the value, in the order the lines come in the document.</param>
/// <param name="Possibly">
/// Each line that might have changed a value in memory between the caret and a reader, in the
/// order the lines come in the document. Only memory has such lines.
/// </param>
internal sealed record SourcesOutput(string Name, string? Group, string Category, IReadOnlyList<ReaderSpan> Readers, IReadOnlyList<Range> Possibly);
