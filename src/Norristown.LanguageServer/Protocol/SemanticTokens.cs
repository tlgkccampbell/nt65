namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// The classified tokens of a document, five numbers each: the line from the previous token's,
/// the character from its start when on the same line, the length, the type and the modifier
/// bits.
/// </summary>
internal sealed record SemanticTokens(IReadOnlyList<int> Data);
