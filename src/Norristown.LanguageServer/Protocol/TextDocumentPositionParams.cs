namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a position in a document, which is what the <c>hover</c>, <c>definition</c>,
/// <c>documentHighlight</c> and <c>prepareRename</c> requests ask about.
/// </summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Position">The position in the document.</param>
internal record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);
