namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents one document's share of a workspace edit, with the version the edits were computed
/// against.
/// </summary>
/// <param name="TextDocument">The document, and the version the edits are against.</param>
/// <param name="Edits">The edits to apply to the document.</param>
internal sealed record TextDocumentEdit(
    OptionalVersionedTextDocumentIdentifier TextDocument, IReadOnlyList<TextEdit> Edits);
