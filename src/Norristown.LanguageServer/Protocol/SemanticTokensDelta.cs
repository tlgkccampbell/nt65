namespace Norristown.LanguageServer.Protocol;

/// <summary>What changed since the answer the client is holding.</summary>
/// <param name="ResultId">The id to quote for this answer when asking for the next change to it.</param>
/// <param name="Edits">The runs of numbers that changed; a single contiguous edit produces at most one.</param>
internal sealed record SemanticTokensDelta(string? ResultId, IReadOnlyList<SemanticTokensEdit> Edits);