namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>textDocument/semanticTokens/range</c> request, which clients use for long
/// files.
/// </summary>
/// <param name="TextDocument">The document to classify.</param>
/// <param name="Range">The lines the editor is showing.</param>
internal sealed record SemanticTokensRangeParams(TextDocumentIdentifier TextDocument, Range Range);