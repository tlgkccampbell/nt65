namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/foldingRange</c> request.</summary>
/// <param name="TextDocument">The document to find foldable ranges in.</param>
internal sealed record FoldingRangeParams(TextDocumentIdentifier TextDocument);
