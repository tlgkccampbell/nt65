namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/semanticTokens/full</c>.</summary>
/// <param name="TextDocument">The document to classify.</param>
internal sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument);
