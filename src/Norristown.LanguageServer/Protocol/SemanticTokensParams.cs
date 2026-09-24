namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/semanticTokens/full</c> request.</summary>
/// <param name="TextDocument">The document to classify.</param>
internal sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument);
