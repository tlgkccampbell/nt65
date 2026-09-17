namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server classifies the names in a document.</summary>
/// <param name="Legend">What its numbers mean.</param>
/// <param name="Full">Whether it answers <c>textDocument/semanticTokens/full</c>.</param>
internal sealed record SemanticTokensOptions(SemanticTokensLegend Legend, bool Full);
