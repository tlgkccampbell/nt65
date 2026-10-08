using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one loop of a routine that an editor draws as a bracket over the lines of the
/// loop's blocks. <see cref="LoopBrackets"/> says which loops these are.
/// </summary>
/// <param name="Top">The span of the first line of the file that a block of the loop is on.</param>
/// <param name="Bottom">The span of the last line of the file that a block of the loop is on.</param>
/// <param name="Header">The span of the line of the block every iteration starts at.</param>
/// <param name="Latches">The span of each line that branches or jumps back to the header, in the order of the lines.</param>
/// <param name="Trips">
/// How many times the loop runs each time it is entered, where it is a counted loop, or null
/// where the program does not say.
/// </param>
public sealed record LoopBracket(TextSpan Top, TextSpan Bottom, TextSpan Header, IReadOnlyList<TextSpan> Latches, int? Trips);
