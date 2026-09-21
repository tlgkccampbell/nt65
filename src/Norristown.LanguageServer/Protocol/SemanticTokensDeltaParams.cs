namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/semanticTokens/full/delta</c>.</summary>
/// <param name="TextDocument">The document to classify.</param>
/// <param name="PreviousResultId">Which answer the client is holding, which the new one is a change to.</param>
internal sealed record SemanticTokensDeltaParams(TextDocumentIdentifier TextDocument, string PreviousResultId);