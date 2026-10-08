namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the answer to an <c>nt65/flowArrows</c> request, which lists the transfers of
/// control inside the routine that holds the caret for the client to draw as arrows. Every line
/// in it is a line of the caret's document.
/// </summary>
/// <param name="First">The line that opens the routine.</param>
/// <param name="Last">The line that closes the routine.</param>
/// <param name="Columns">
/// How many columns the arrows take, at most <see cref="CaretFlow.MostColumns"/>. The client
/// draws the same number on every line of the routine, so that the code does not jog.
/// </param>
/// <param name="Arrows">Each transfer, in the order of the lines that make them.</param>
internal sealed record FlowArrowsResult(int First, int Last, int Columns, IReadOnlyList<FlowArrowsItem> Arrows);
