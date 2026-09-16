namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// A place in a document, which is what <c>hover</c>, <c>definition</c>,
/// <c>documentHighlight</c> and <c>prepareRename</c> all ask about.
/// </summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Position">Where in it.</param>
internal record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);
