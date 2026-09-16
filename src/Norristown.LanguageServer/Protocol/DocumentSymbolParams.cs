namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/documentSymbol</c>.</summary>
/// <param name="TextDocument">The document to outline.</param>
internal sealed record DocumentSymbolParams(TextDocumentIdentifier TextDocument);
