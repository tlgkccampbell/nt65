namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the changes since the result the client holds.</summary>
/// <param name="ResultId">
/// The identifier for this result, used when requesting the next change to it.
/// </param>
/// <param name="Edits">
/// The runs of numbers that changed. A single contiguous edit produces at most one run.
/// </param>
internal sealed record SemanticTokensDelta(string? ResultId, IReadOnlyList<SemanticTokensEdit> Edits);