namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/rename</c>.</summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Position">The name being renamed.</param>
/// <param name="NewName">What to call it.</param>
internal sealed record RenameParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    string NewName)
    : TextDocumentPositionParams(TextDocument, Position);
