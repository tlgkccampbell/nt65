namespace Norristown.LanguageServer.Protocol;

/// <summary>One document's share of a workspace edit, with the revision it was worked out against.</summary>
/// <param name="TextDocument">The document, and the revision the edits are against.</param>
/// <param name="Edits">What to write in it.</param>
internal sealed record TextDocumentEdit(
    OptionalVersionedTextDocumentIdentifier TextDocument, IReadOnlyList<TextEdit> Edits);
