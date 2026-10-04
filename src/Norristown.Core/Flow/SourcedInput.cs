using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one input of the instruction at the caret, which is one thing the instruction reads,
/// with the places its value was set.
/// </summary>
/// <param name="Name">
/// The input's name, such as <c>A</c> or <c>C</c>. A width is named as a signature spells the width
/// the routine called needs, such as <c>a8</c> or <c>i16</c>.
/// </param>
/// <param name="Group">The root symbol that memory inputs are grouped under, or null for every other input.</param>
/// <param name="Category">What kind of thing the input is.</param>
/// <param name="Sources">Each place the value was set, in the order the lines come in the file.</param>
/// <param name="Through">
/// The span of each line the value passed through unchanged on its way to the caret, in the order
/// the lines come in the file. Such a line is a call that keeps the register, or a pull that
/// restores a value pushed earlier.
/// </param>
public sealed record SourcedInput(
    string Name, string? Group, InputCategory Category, IReadOnlyList<InputSource> Sources, IReadOnlyList<TextSpan> Through);
