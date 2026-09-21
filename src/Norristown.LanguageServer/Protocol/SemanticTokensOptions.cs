namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server classifies the names in a document.</summary>
/// <param name="Legend">What its numbers mean.</param>
/// <param name="Full">How it answers about a whole document, and whether it can answer a change to one.</param>
/// <param name="Range">Whether it answers about the lines the editor is showing, which is what a long file is read by.</param>
internal sealed record SemanticTokensOptions(
    SemanticTokensLegend Legend, SemanticTokensFullOptions Full, bool Range);
