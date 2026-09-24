namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/documentSymbol</c> request.</summary>
/// <param name="TextDocument">The document to outline.</param>
internal sealed record DocumentSymbolParams(TextDocumentIdentifier TextDocument);
