namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/references</c> request.</summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Position">The position of the name to find references to.</param>
/// <param name="Context">Which references to include.</param>
internal sealed record ReferenceParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    ReferenceContext Context)
    : TextDocumentPositionParams(TextDocument, Position);
