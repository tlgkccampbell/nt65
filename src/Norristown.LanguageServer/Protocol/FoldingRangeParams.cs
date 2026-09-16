namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/foldingRange</c>.</summary>
/// <param name="TextDocument">The document to find foldable ranges in.</param>
internal sealed record FoldingRangeParams(TextDocumentIdentifier TextDocument);
