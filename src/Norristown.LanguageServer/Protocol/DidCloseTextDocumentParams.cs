namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/didClose</c> notification.</summary>
/// <param name="TextDocument">The document the client no longer has open.</param>
internal sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);
