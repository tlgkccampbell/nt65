namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/semanticTokens/full/delta</c> request.</summary>
/// <param name="TextDocument">The document to classify.</param>
/// <param name="PreviousResultId">
/// The identifier of the result the client holds, which the new result is a change to.
/// </param>
internal sealed record SemanticTokensDeltaParams(TextDocumentIdentifier TextDocument, string PreviousResultId);