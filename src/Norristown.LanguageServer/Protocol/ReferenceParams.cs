namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/references</c>.</summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Position">The name to look for.</param>
/// <param name="Context">What to include.</param>
internal sealed record ReferenceParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    ReferenceContext Context)
    : TextDocumentPositionParams(TextDocument, Position);
