namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one transfer of control that the client draws as an arrow.</summary>
/// <param name="From">The line that transfers.</param>
/// <param name="To">The line control goes to.</param>
/// <param name="Column">
/// The column the arrow's upright is drawn in, where 0 is the one nearest the code, or null where
/// it found no free column. Such an arrow is not drawn, and the client names it in the hover.
/// </param>
/// <param name="Declared">Whether a <c>.next</c> declared the transfer.</param>
/// <param name="Proved">Whether the transfer is a conditional branch the flags prove always or never taken.</param>
/// <param name="Reached">Whether any path from the routine's entry reaches the line that transfers.</param>
internal sealed record MarginArrow(int From, int To, int? Column, bool Declared, bool Proved, bool Reached);
