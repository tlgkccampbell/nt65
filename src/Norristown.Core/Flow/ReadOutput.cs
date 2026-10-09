using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one output of the instruction at the caret, which is one value it writes, with the
/// places that read it.
/// </summary>
/// <param name="Name">
/// The output's name, such as <c>A</c> or <c>C</c>, a width as a signature spells the one a routine
/// called needs, such as <c>a16</c>, or a symbol's name for a location in memory.
/// </param>
/// <param name="Group">The root symbol that memory outputs are grouped under, or null for every other output.</param>
/// <param name="Category">What kind of thing the output is.</param>
/// <param name="Readers">Each place that reads the value, in the order the lines come in the file.</param>
/// <param name="Possibly">
/// The span of each line that might have changed a value in memory between the caret and one of
/// its readers, in the order the lines come in the file. Such a line is what
/// <see cref="SourcedInput.Possibly"/> describes for an input. Only memory has such lines.
/// </param>
public sealed record ReadOutput(
    string Name, string? Group, InputCategory Category, IReadOnlyList<OutputReader> Readers, IReadOnlyList<TextSpan> Possibly);
