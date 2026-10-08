namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one transfer of control that the client draws as an arrow.</summary>
/// <param name="From">The line that transfers.</param>
/// <param name="To">The line control goes to, or null where control leaves the routine.</param>
/// <param name="Column">
/// The column the arrow's upright is drawn in, where 0 is the one nearest the code, or null. An
/// arrow that leaves the routine has no upright. One that found no free column is not drawn, and
/// the client names it in the hover instead.
/// </param>
/// <param name="Declared">Whether a <c>.next</c> declared the transfer.</param>
/// <param name="Proved">Whether the transfer is a conditional branch the flags prove always or never taken.</param>
/// <param name="Reached">Whether any path from the routine's entry reaches the line that transfers.</param>
internal sealed record FlowArrowsItem(int From, int? To, int? Column, bool Declared, bool Proved, bool Reached);
