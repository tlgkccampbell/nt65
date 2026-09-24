namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/didChange</c> notification.</summary>
/// <param name="TextDocument">The document, at the version the changes produce.</param>
/// <param name="ContentChanges">The edits, which apply in order.</param>
internal sealed record DidChangeTextDocumentParams(
    VersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges);
