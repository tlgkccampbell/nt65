namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/rename</c> request.</summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Position">The position of the name being renamed.</param>
/// <param name="NewName">The new name.</param>
internal sealed record RenameParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    string NewName)
    : TextDocumentPositionParams(TextDocument, Position);
