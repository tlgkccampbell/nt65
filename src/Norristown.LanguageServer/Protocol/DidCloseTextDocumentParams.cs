namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/didClose</c>.</summary>
/// <param name="TextDocument">The document the client no longer has open.</param>
internal sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);
