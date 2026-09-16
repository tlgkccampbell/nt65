namespace Norristown.LanguageServer.Protocol;

/// <summary>What the server can do. Stages add to this as the layers come online.</summary>
/// <param name="TextDocumentSync">How open documents are kept in step.</param>
/// <param name="DocumentSymbolProvider">Whether the server answers <c>textDocument/documentSymbol</c>.</param>
/// <param name="FoldingRangeProvider">Whether the server answers <c>textDocument/foldingRange</c>.</param>
internal sealed record ServerCapabilities(
    TextDocumentSyncOptions TextDocumentSync,
    bool DocumentSymbolProvider,
    bool FoldingRangeProvider);
