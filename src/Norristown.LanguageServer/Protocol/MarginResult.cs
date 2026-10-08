namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the answer to an <c>nt65/margin</c> request. It lists each routine of the document
/// that has something to draw in front of its lines, which is the brackets of its loops and, for
/// the routine at the caret, the arrows of its branches and jumps. Every line in it is a line of
/// the document.
/// </summary>
/// <param name="Routines">Each routine with something to draw, in the order of their first lines.</param>
internal sealed record MarginResult(IReadOnlyList<MarginRoutine> Routines);
