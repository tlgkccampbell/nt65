namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents what the client draws in front of the lines of one routine.</summary>
/// <param name="First">The line that opens the routine.</param>
/// <param name="Last">The line that closes the routine.</param>
/// <param name="Columns">
/// How many columns the brackets and arrows take, at most <see cref="Margin.MostColumns"/>. The
/// client draws the same number on every line of the routine, so that the code does not jog.
/// </param>
/// <param name="Brackets">Each loop, in the order of their first lines.</param>
/// <param name="Arrows">Each transfer of control, in the order of the lines that make them, or none for a routine away from the caret.</param>
internal sealed record MarginRoutine(
    int First, int Last, int Columns, IReadOnlyList<MarginBracket> Brackets, IReadOnlyList<MarginArrow> Arrows);
