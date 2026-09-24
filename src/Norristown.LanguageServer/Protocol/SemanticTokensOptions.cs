namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server classifies the names in a document.</summary>
/// <param name="Legend">The meaning of the server's numbers.</param>
/// <param name="Full">
/// How the server provides tokens for a whole document, and whether it can return changes to them.
/// </param>
/// <param name="Range">
/// Whether the server provides tokens for only the lines the editor is showing, which clients use
/// for long files.
/// </param>
internal sealed record SemanticTokensOptions(
    SemanticTokensLegend Legend, SemanticTokensFullOptions Full, bool Range);
