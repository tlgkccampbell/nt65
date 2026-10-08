namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents an <c>nt65/margin</c> request, which asks what to draw in front of a document's lines.</summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Position">The caret, which picks the routine whose arrows are drawn.</param>
/// <param name="Brackets">Whether the client draws the brackets of every routine's loops.</param>
/// <param name="Arrows">Whether the client draws the arrows of the caret's routine.</param>
internal sealed record MarginParams(TextDocumentIdentifier TextDocument, Position Position, bool Brackets, bool Arrows);
