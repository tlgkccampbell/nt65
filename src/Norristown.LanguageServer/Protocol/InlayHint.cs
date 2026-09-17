namespace Norristown.LanguageServer.Protocol;

/// <summary>Text the client shows in a line without it being part of the document.</summary>
/// <param name="Position">Where it is shown.</param>
/// <param name="Label">The text.</param>
/// <param name="PaddingLeft">Whether a space separates it from the text before.</param>
internal sealed record InlayHint(Position Position, string Label, bool PaddingLeft);
