namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the classified tokens of a document, encoded as five numbers per token. The numbers
/// are the line relative to the previous token's line, the character relative to the previous
/// token's start when both are on the same line, the length, the type and the modifier bits.
/// </summary>
/// <param name="Data">The encoded numbers.</param>
/// <param name="ResultId">
/// An identifier for this result, used when requesting the next change to it, or null when the
/// result covers part of a document and no change can be requested.
/// </param>
internal sealed record SemanticTokens(IReadOnlyList<int> Data, string? ResultId = null);
