namespace Norristown.LanguageServer.Protocol;

/// <summary>What changed since the answer the client is holding.</summary>
/// <param name="ResultId">What to call this answer when asking for the next change to it.</param>
/// <param name="Edits">The runs of numbers that moved, at most one for an edit in one place.</param>
internal sealed record SemanticTokensDelta(string? ResultId, IReadOnlyList<SemanticTokensEdit> Edits);