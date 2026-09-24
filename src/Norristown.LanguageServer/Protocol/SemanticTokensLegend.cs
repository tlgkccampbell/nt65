namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the names that a token's type and modifier numbers index.</summary>
/// <param name="TokenTypes">The token types, by number.</param>
/// <param name="TokenModifiers">The modifiers, by bit.</param>
internal sealed record SemanticTokensLegend(IReadOnlyList<string> TokenTypes, IReadOnlyList<string> TokenModifiers);
