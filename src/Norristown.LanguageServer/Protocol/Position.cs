namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a position in a document. Both values are zero-based, and a character is a UTF-16
/// code unit.
/// </summary>
/// <param name="Line">The line.</param>
/// <param name="Character">The offset within the line.</param>
internal sealed record Position(int Line, int Character);
