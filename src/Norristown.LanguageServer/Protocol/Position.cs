namespace Norristown.LanguageServer.Protocol;

/// <summary>A place in a document. Both are 0-based, and a character is a UTF-16 code unit.</summary>
/// <param name="Line">The line.</param>
/// <param name="Character">The offset within the line.</param>
internal sealed record Position(int Line, int Character);
