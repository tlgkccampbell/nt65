namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one loop that the client draws as a bracket over its lines, with its trip count after the last.</summary>
/// <param name="Top">The first line of the loop.</param>
/// <param name="Bottom">The last line of the loop, which the trip count is drawn after.</param>
/// <param name="Head">
/// Whether the loop's header is its first line. The bracket then stands for the branches back to
/// the header, which are not sent as arrows, and its top ends in an arrowhead.
/// </param>
/// <param name="Tees">The lines above the bottom that branch back to the header, which join the bracket where it has a head.</param>
/// <param name="Column">
/// The column the bracket's upright is drawn in, where 0 is the one nearest the code, or null
/// where the loop is on one line or found no free column. Only its trip count is drawn then.
/// </param>
/// <param name="Trips">How many times a counted loop runs each time it is entered, or null where the program does not say.</param>
internal sealed record MarginBracket(int Top, int Bottom, bool Head, IReadOnlyList<int> Tees, int? Column, int? Trips);
