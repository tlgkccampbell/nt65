namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/didChange</c>.</summary>
/// <param name="TextDocument">The document, at the revision the changes produce.</param>
/// <param name="ContentChanges">The edits, to apply in order.</param>
internal sealed record DidChangeTextDocumentParams(
    VersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges);
