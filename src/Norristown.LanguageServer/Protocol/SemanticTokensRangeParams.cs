namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/semanticTokens/range</c>, which is what a long file is read by.</summary>
/// <param name="TextDocument">The document to classify.</param>
/// <param name="Range">The lines the editor is showing.</param>
internal sealed record SemanticTokensRangeParams(TextDocumentIdentifier TextDocument, Range Range);