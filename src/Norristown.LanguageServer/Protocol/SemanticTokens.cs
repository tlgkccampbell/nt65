namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// The classified tokens of a document, five numbers each: the line from the previous token's,
/// the character from its start when on the same line, the length, the type and the modifier
/// bits.
/// </summary>
/// <param name="Data">The numbers.</param>
/// <param name="ResultId">
/// What to call this answer when asking for the next change to it, or null where the answer is
/// about part of a document and there is no change to ask for.
/// </param>
internal sealed record SemanticTokens(IReadOnlyList<int> Data, string? ResultId = null);
