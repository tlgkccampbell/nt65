namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one input of the instruction at the caret, with the places its value was set.</summary>
/// <param name="Name">
/// The input's name: <c>A</c>, <c>X</c>, <c>Y</c>, <c>C</c>, <c>N</c>, <c>Z</c>, <c>V</c>, <c>M</c>
/// or <c>X width</c>, or a symbol's name for a location in memory.
/// </param>
/// <param name="Group">The root symbol a memory input is grouped under, such as <c>banks</c> for <c>banks::source</c>, or null.</param>
/// <param name="Category">The input's kind: <c>register</c>, <c>flag</c>, <c>width</c> or <c>memory</c>.</param>
/// <param name="Sources">Each place the value was set, in the order the lines come in the document.</param>
/// <param name="Through">Each line the value passed through unchanged, in the order the lines come in the document.</param>
internal sealed record SourcesInput(
    string Name, string? Group, string Category, IReadOnlyList<SourceSpan> Sources, IReadOnlyList<Range> Through);
